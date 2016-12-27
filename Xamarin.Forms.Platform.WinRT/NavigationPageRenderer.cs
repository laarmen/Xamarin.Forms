﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Input;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Xamarin.Forms.Internals;

#if WINDOWS_UWP
using Windows.UI.Core;
namespace Xamarin.Forms.Platform.UWP
#else

namespace Xamarin.Forms.Platform.WinRT
#endif
{
	public partial class NavigationPageRenderer : IVisualElementRenderer, ITitleProvider, IToolbarProvider
	{
		PageControl _container;
		Page _currentPage;
		Page _previousPage;

		bool _disposed;

		MasterDetailPage _parentMasterDetailPage;
		TabbedPage _parentTabbedPage;
		bool _showTitle = true;
		VisualElementTracker<Page, PageControl> _tracker;
		ContentThemeTransition _transition;

		public NavigationPage Element { get; private set; }

		protected VisualElementTracker<Page, PageControl> Tracker
		{
			get { return _tracker; }
			set
			{
				if (_tracker == value)
					return;

				if (_tracker != null)
					_tracker.Dispose();

				_tracker = value;
			}
		}

		public void Dispose()
		{
			Dispose(true);
		}

		Brush ITitleProvider.BarBackgroundBrush
		{
			set
			{
				_container.ToolbarBackground = value;
				UpdateTitleOnParents();
			}
		}

		Brush ITitleProvider.BarForegroundBrush
		{
			set
			{
				_container.TitleBrush = value;
				UpdateTitleOnParents();
			}
		}

		IPageController PageController => Element as IPageController;

		bool ITitleProvider.ShowTitle
		{
			get { return _showTitle; }
			set
			{
				if (_showTitle == value)
					return;

				_showTitle = value;
				UpdateTitleVisible();
				UpdateTitleOnParents();
			}
		}

		public string Title
		{
			get { return _currentPage?.Title; }

			set { }
		}

		Task<CommandBar> IToolbarProvider.GetCommandBarAsync()
		{
			return ((IToolbarProvider)_container)?.GetCommandBarAsync();
		}

		public FrameworkElement ContainerElement
		{
			get { return _container; }
		}

		VisualElement IVisualElementRenderer.Element
		{
			get { return Element; }
		}

		public event EventHandler<VisualElementChangedEventArgs> ElementChanged;

		public SizeRequest GetDesiredSize(double widthConstraint, double heightConstraint)
		{
			var constraint = new Windows.Foundation.Size(widthConstraint, heightConstraint);
			IVisualElementRenderer childRenderer = Platform.GetRenderer(Element.CurrentPage);
			FrameworkElement child = childRenderer.ContainerElement;

			double oldWidth = child.Width;
			double oldHeight = child.Height;

			child.Height = double.NaN;
			child.Width = double.NaN;

			child.Measure(constraint);
			var result = new Size(Math.Ceiling(child.DesiredSize.Width), Math.Ceiling(child.DesiredSize.Height));

			child.Width = oldWidth;
			child.Height = oldHeight;

			return new SizeRequest(result);
		}

		public void SetElement(VisualElement element)
		{
			if (element != null && !(element is NavigationPage))
				throw new ArgumentException("Element must be a Page", nameof(element));

			NavigationPage oldElement = Element;
			Element = (NavigationPage)element;

			if (oldElement != null)
			{
				((INavigationPageController)oldElement).PushRequested -= OnPushRequested;
				((INavigationPageController)oldElement).PopRequested -= OnPopRequested;
				((INavigationPageController)oldElement).PopToRootRequested -= OnPopToRootRequested;
				((IPageController)oldElement).InternalChildren.CollectionChanged -= OnChildrenChanged;
				oldElement.PropertyChanged -= OnElementPropertyChanged;
			}

			if (element != null)
			{
				if (_container == null)
				{
					_container = new PageControl();
					_container.PointerPressed += OnPointerPressed;
					_container.SizeChanged += OnNativeSizeChanged;
					_container.BackClicked += OnBackClicked;

					Tracker = new BackgroundTracker<PageControl>(Control.BackgroundProperty) { Element = (Page)element, Container = _container };

					SetPage(Element.CurrentPage, false, false);

					_container.Loaded += OnLoaded;
					_container.Unloaded += OnUnloaded;
				}

				_container.DataContext = Element.CurrentPage;

				UpdatePadding();
				LookupRelevantParents();
				UpdateTitleColor();
				UpdateNavigationBarBackground();
				UpdateToolbarPlacement();

				Element.PropertyChanged += OnElementPropertyChanged;
				((INavigationPageController)Element).PushRequested += OnPushRequested;
				((INavigationPageController)Element).PopRequested += OnPopRequested;
				((INavigationPageController)Element).PopToRootRequested += OnPopToRootRequested;
				PageController.InternalChildren.CollectionChanged += OnChildrenChanged;

				if (!string.IsNullOrEmpty(Element.AutomationId))
					_container.SetValue(AutomationProperties.AutomationIdProperty, Element.AutomationId);

				PushExistingNavigationStack();
			}

			OnElementChanged(new VisualElementChangedEventArgs(oldElement, element));
		}

		protected void Dispose(bool disposing)
		{
			if (_disposed || !disposing)
			{
				return;
			}

			PageController?.SendDisappearing();
			_disposed = true;

			_container.PointerPressed -= OnPointerPressed;
			_container.SizeChanged -= OnNativeSizeChanged;
			_container.BackClicked -= OnBackClicked;

			SetElement(null);
			SetPage(null, false, true);
			_previousPage = null;

			if (_parentTabbedPage != null)
				_parentTabbedPage.PropertyChanged -= MultiPagePropertyChanged;

			if (_parentMasterDetailPage != null)
				_parentMasterDetailPage.PropertyChanged -= MultiPagePropertyChanged;

#if WINDOWS_UWP
			if (_navManager != null)
			{
				_navManager.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;
			}
#endif
		}

		protected void OnElementChanged(VisualElementChangedEventArgs e)
		{
			EventHandler<VisualElementChangedEventArgs> changed = ElementChanged;
			if (changed != null)
				changed(this, e);
		}

		Brush GetBarBackgroundBrush()
		{
			object defaultColor = GetDefaultColor();

			if (Element.BarBackgroundColor.IsDefault && defaultColor != null)
				return (Brush)defaultColor;
			return Element.BarBackgroundColor.ToBrush();
		}

		Brush GetBarForegroundBrush()
		{
			object defaultColor = Windows.UI.Xaml.Application.Current.Resources["ApplicationForegroundThemeBrush"];
			if (Element.BarTextColor.IsDefault)
				return (Brush)defaultColor;
			return Element.BarTextColor.ToBrush();
		}

		bool GetIsNavBarPossible()
		{
			return _showTitle;
		}

		void LookupRelevantParents()
		{
			IEnumerable<Page> parentPages = Element.GetParentPages();

			if (_parentTabbedPage != null)
				_parentTabbedPage.PropertyChanged -= MultiPagePropertyChanged;
			if (_parentMasterDetailPage != null)
				_parentMasterDetailPage.PropertyChanged -= MultiPagePropertyChanged;

			foreach (Page parentPage in parentPages)
			{
				_parentTabbedPage = parentPage as TabbedPage;
				_parentMasterDetailPage = parentPage as MasterDetailPage;
			}

			if (_parentTabbedPage != null)
				_parentTabbedPage.PropertyChanged += MultiPagePropertyChanged;
			if (_parentMasterDetailPage != null)
				_parentMasterDetailPage.PropertyChanged += MultiPagePropertyChanged;

			UpdateShowTitle();

			UpdateTitleOnParents();
		}

		void MultiPagePropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == "CurrentPage" || e.PropertyName == "Detail")
				UpdateTitleOnParents();
		}

		async void OnBackClicked(object sender, RoutedEventArgs e)
		{
			await Element.PopAsync();
		}

		void OnChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			UpdateBackButton();
		}

		void OnCurrentPagePropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == NavigationPage.HasBackButtonProperty.PropertyName)
				UpdateBackButton();
			else if (e.PropertyName == NavigationPage.BackButtonTitleProperty.PropertyName)
				UpdateBackButtonTitle();
			else if (e.PropertyName == NavigationPage.HasNavigationBarProperty.PropertyName)
				UpdateTitleVisible();
		}

		void OnElementPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == NavigationPage.BarTextColorProperty.PropertyName)
				UpdateTitleColor();
			else if (e.PropertyName == NavigationPage.BarBackgroundColorProperty.PropertyName)
				UpdateNavigationBarBackground();
			else if (e.PropertyName == Page.PaddingProperty.PropertyName)
				UpdatePadding();
#if WINDOWS_UWP
			else if (e.PropertyName == PlatformConfiguration.WindowsSpecific.Page.ToolbarPlacementProperty.PropertyName)
				UpdateToolbarPlacement();
#endif
		}

		void OnLoaded(object sender, RoutedEventArgs args)
		{
			if (Element == null)
				return;

#if WINDOWS_UWP
			_navManager = SystemNavigationManager.GetForCurrentView();
#endif
			PageController.SendAppearing();
			UpdateBackButton();
			UpdateTitleOnParents();
		}

		void OnNativeSizeChanged(object sender, SizeChangedEventArgs e)
		{
			UpdateContainerArea();
		}

		void OnPointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (e.Handled)
				return;

			PointerPoint point = e.GetCurrentPoint(_container);
			if (point == null)
				return;

			if (point.PointerDevice.PointerDeviceType != PointerDeviceType.Mouse)
				return;

			if (point.Properties.IsXButton1Pressed)
			{
				e.Handled = true;
				OnBackClicked(_container, e);
			}
		}

		void OnPopRequested(object sender, NavigationRequestedEventArgs e)
		{
			var newCurrent = (Page)PageController.InternalChildren[PageController.InternalChildren.Count - 2];
			SetPage(newCurrent, e.Animated, true);
		}

		void OnPopToRootRequested(object sender, NavigationRequestedEventArgs e)
		{
			SetPage(e.Page, e.Animated, true);
		}

		void OnPushRequested(object sender, NavigationRequestedEventArgs e)
		{
			SetPage(e.Page, e.Animated, false);
		}

		void OnUnloaded(object sender, RoutedEventArgs args)
		{
			PageController?.SendDisappearing();
		}

		void PushExistingNavigationStack()
		{
			for (int i = ((INavigationPageController)Element).StackCopy.Count - 1; i >= 0; i--)
				SetPage(((INavigationPageController)Element).StackCopy.ElementAt(i), false, false);
		}

		void SetPage(Page page, bool isAnimated, bool isPopping)
		{
			if (_currentPage != null)
			{
				if (isPopping)
					_currentPage.Cleanup();

				_container.Content = null;

				_currentPage.PropertyChanged -= OnCurrentPagePropertyChanged;
			}

			if (!isPopping)
				_previousPage = _currentPage;

			_currentPage = page;

			if (page == null)
				return;

			UpdateBackButton();
			UpdateBackButtonTitle();

			page.PropertyChanged += OnCurrentPagePropertyChanged;

			IVisualElementRenderer renderer = page.GetOrCreateRenderer();

			UpdateTitleVisible();
			UpdateTitleOnParents();

			if (isAnimated && _transition == null)
			{
				_transition = new ContentThemeTransition();
				_container.ContentTransitions = new TransitionCollection();
			}

			if (!isAnimated && _transition != null)
				_container.ContentTransitions.Remove(_transition);
			else if (isAnimated && _container.ContentTransitions.Count == 0)
				_container.ContentTransitions.Add(_transition);

			_container.Content = renderer.ContainerElement;
			_container.DataContext = page;
		}

		void UpdateBackButtonTitle()
		{
			string title = null;
			if (_previousPage != null)
				title = NavigationPage.GetBackButtonTitle(_previousPage);

			_container.BackButtonTitle = title;
		}

		void UpdateContainerArea()
		{
			PageController.ContainerArea = new Rectangle(0, 0, _container.ContentWidth, _container.ContentHeight);
		}

		void UpdateNavigationBarBackground()
		{
			(this as ITitleProvider).BarBackgroundBrush = GetBarBackgroundBrush();
		}

		void UpdateTitleVisible()
		{
			UpdateTitleOnParents();

			bool showing = _container.TitleVisibility == Visibility.Visible;
			bool newValue = GetIsNavBarPossible() && NavigationPage.GetHasNavigationBar(_currentPage);
			if (showing == newValue)
				return;

			_container.TitleVisibility = newValue ? Visibility.Visible : Visibility.Collapsed;

			// Force ContentHeight/Width to update, doesn't work from inside PageControl for some reason
			_container.UpdateLayout();
			UpdateContainerArea();
		}

		void UpdatePadding()
		{
			_container.TitleInset = Element.Padding.Left;
		}

		void UpdateTitleColor()
		{
			(this as ITitleProvider).BarForegroundBrush = GetBarForegroundBrush();
		}
	}
}