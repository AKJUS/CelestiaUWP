﻿using CelestiaComponent;
using CelestiaUWP.Helper;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace CelestiaUWP
{
    public sealed partial class MainPage : Page
    {
        private CelestiaAppCore mAppCore;
        private CelestiaRenderer mRenderer;

        private static int leftMouseButton = 1;
        private static int middleMouseButton = 2;
        private static int rightMouseButton = 4;

        private Point? mLastLeftMousePosition = null;
        private Point? mLastRightMousePosition = null;

        private string mCurrentPath;
        private string mExtraAddonFolder;
        private string mExtraScriptFolder;

        private Dictionary<string, object> mSettings;

        private readonly string[] mMarkers = new string[]
        {
            "Diamond", "Triangle", "Filled Square", "Plus", "X", "Left Arrow", "Right Arrow", "Up Arrow", "Down Arrow",
            "Circle", "Disk", "Crosshair"
        };

        private string mLocalePath
        {
            get { return mCurrentPath + "\\locale"; }
        }

        private float scale = 1.0f;

        public MainPage()
        {
            mAppCore = new CelestiaAppCore();

            InitializeComponent();

            scale = ((int)Windows.Graphics.Display.DisplayInformation.GetForCurrentView().ResolutionScale) / 100.0f;

            string installedPath = Windows.ApplicationModel.Package.Current.InstalledPath;
            mCurrentPath = installedPath + "\\CelestiaResources";
            Directory.SetCurrentDirectory(mCurrentPath);

            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            mRenderer = new CelestiaRenderer(() => {
                LocalizationHelper.Locale = GetLocale().Result;
                CelestiaAppCore.SetLocaleDirectory(mLocalePath, LocalizationHelper.Locale);

                CelestiaAppCore.InitGL();

                CreateExtraFolders();
                List<string> extraPaths = new List<string>();
                if (mExtraAddonFolder != null)
                    extraPaths.Add(mExtraAddonFolder);

                if (!mAppCore.StartSimulation("celestia.cfg", extraPaths.ToArray(), delegate (string progress)
                {
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        LoadingText.Text = string.Format(LocalizationHelper.Localize("Loading: %s").Replace("%s", "{0}"), progress);
                    });
                }))
                {
                    return false;
                }
                mAppCore.SetDPI((int)(96 * scale));
                if (!mAppCore.StartRenderer())
                    return false;

                var fontMap = new Dictionary<string, (string, int, string, int)>() {
                    { "ja", ("NotoSansCJK-Regular.ttc", 0, "NotoSansCJK-Bold.ttc", 0) },
                    { "ko", ("NotoSansCJK-Regular.ttc", 1, "NotoSansCJK-Bold.ttc", 1) },
                    { "zh_CN", ("NotoSansCJK-Regular.ttc", 2, "NotoSansCJK-Bold.ttc", 2) },
                    { "zh_TW", ("NotoSansCJK-Regular.ttc", 3, "NotoSansCJK-Bold.ttc", 3) },
                    { "ar", ("NotoSansArabic-Regular.ttf", 0, "NotoSansArabic-Bold.ttf", 0) },
                };
                var defaultFont = ("NotoSans-Regular.ttf", 0, "NotoSans-Bold.ttf", 0);
                var font = fontMap.GetValueOrDefault(LocalizationHelper.Locale, defaultFont);

                mAppCore.SetFont(mCurrentPath + "\\fonts\\" + font.Item1, font.Item2, 9);
                mAppCore.SetTitleFont(mCurrentPath + "\\fonts\\" + font.Item3, font.Item4, 15);
                mAppCore.SetRenderFont(mCurrentPath + "\\fonts\\" + font.Item1, font.Item2, 9, CelestiaFontStyle.normal);
                mAppCore.SetRenderFont(mCurrentPath + "\\fonts\\" + font.Item3, font.Item4, 15, CelestiaFontStyle.large);

                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    LoadingText.Visibility = Visibility.Collapsed;
                    SetUpGLViewInteractions();
                    PopulateMenuBar();
                    mRenderer.SetSize((int)GLView.Width, (int)GLView.Height);
                });

                mSettings = ReadSettings().Result;
                ApplySettings(mSettings);

                mAppCore.Start();
                return true;
            });
            mRenderer.SetCorePointer(mAppCore.Pointer);
            mRenderer.SetSurface(GLView, scale);
            GLView.SizeChanged += (view, arg) =>
            {
                mRenderer.SetSize((int)arg.NewSize.Width, (int)arg.NewSize.Height);
            };
            mRenderer.Start();
        }

        void CreateExtraFolders()
        {
            var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
            var addonPath = folder.Path + "\\CelestiaResources\\extras";
            var scriptsPath = folder.Path + "\\CelestiaResources\\scripts";

            try
            {
                Directory.CreateDirectory(addonPath);
                mExtraAddonFolder = addonPath;
            } catch { }

            try
            {
                Directory.CreateDirectory(scriptsPath);
                mExtraScriptFolder = scriptsPath;
            }
            catch { }
        }

        void SetUpGLViewInteractions()
        {
            Window.Current.VisibilityChanged += (sender, args) =>
            {
                SaveSettings(GetCurrentSettings());
            };

            mAppCore.SetContextMenuHandler((x, y, selection) =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                  {
                      var menu = new MenuFlyout();
                      var nameItem = new MenuFlyoutItem();
                      nameItem.IsEnabled = false;
                      nameItem.Text = mAppCore.Simulation.Universe.NameForSelection(selection);
                      menu.Items.Add(nameItem);
                      menu.Items.Add(new MenuFlyoutSeparator());
                      var actions = new (string, short)[]
                      {
                        ("Go", 103),
                        ("Follow", 102),
                        ("Orbit Synchronously", 121),
                        ("Lock Phase", 58),
                        ("Chase", 34),
                        ("Track", 116)
                      };

                      foreach (var action in actions)
                      {
                          var item = new MenuFlyoutItem();
                          item.Text = LocalizationHelper.Localize(action.Item1);
                          item.Click += (sender, arg) =>
                          {
                              mAppCore.Simulation.Selection = selection;
                              mAppCore.CharEnter(action.Item2);
                          };
                          menu.Items.Add(item);
                      }

                      if (selection.Object is CelestiaBody)
                      {
                          var body = (CelestiaBody)selection.Object;
                          var surfaces = body.AlternateSurfaceNames;
                          if (surfaces != null && surfaces.Length > 0)
                          {
                              menu.Items.Add(new MenuFlyoutSeparator());

                              var altSur = new MenuFlyoutSubItem();
                              altSur.Text = LocalizationHelper.Localize("Alternate Surfaces");
                              AppendSubItem(altSur, LocalizationHelper.Localize("Default"), (sender, arg) =>
                              {
                                  mAppCore.Simulation.ActiveObserver.DisplayedSurfaceName = "";
                              });

                              foreach (var name in surfaces)
                              {
                                  AppendSubItem(altSur, name, (sender, arg) =>
                                  {
                                      mAppCore.Simulation.ActiveObserver.DisplayedSurfaceName = name;
                                  });
                              }

                              menu.Items.Add(altSur);
                          }
                      }

                      var browserMenuItems = new List<MenuFlyoutItemBase>();
                      var browserItem = new CelestiaBrowserItem(mAppCore.Simulation.Universe.NameForSelection(selection), selection.Object, GetChildren);
                      foreach (var child in browserItem.Children)
                      {
                          browserMenuItems.Add(CreateMenuItem(child));
                      }

                      if (browserMenuItems.Count > 0)
                      {
                          menu.Items.Add(new MenuFlyoutSeparator());
                          foreach (var browserMenuItem in browserMenuItems)
                          {
                              menu.Items.Add(browserMenuItem);
                          }
                      }

                      menu.Items.Add(new MenuFlyoutSeparator());

                      if (mAppCore.Simulation.Universe.IsSelectionMarked(selection))
                      {
                          var action = new MenuFlyoutItem();
                          action.Text = LocalizationHelper.Localize("Unmark");
                          action.Click += (sender, arg) =>
                          {
                              mAppCore.Simulation.Universe.UnmarkSelection(selection);
                          };
                          menu.Items.Add(action);
                      }
                      else
                      {
                          var action = new MenuFlyoutSubItem();
                          action.Text = LocalizationHelper.Localize("Mark");
                          for (int i = 0; i < mMarkers.Length; i += 1)
                          {
                              int copy = i;
                              var markerAction = new MenuFlyoutItem();
                              markerAction.Text = LocalizationHelper.Localize(mMarkers[i]);
                              markerAction.Click += (sender, arg) =>
                              {
                                  mAppCore.Simulation.Universe.MarkSelection(selection, (CelestiaMarkerRepresentation)copy);
                                  mAppCore.ShowMarkers = true;
                              };
                              action.Items.Add(markerAction);
                          }
                          menu.Items.Add(action);
                      }

                      menu.ShowAt(GLView, new Point(x / scale, y / scale));
                  });
            });
            GLView.PointerPressed += (sender, args) =>
            {
                if (args.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse)
                {
                    var properties = args.GetCurrentPoint((UIElement)sender).Properties;
                    var position = args.GetCurrentPoint((UIElement)sender).Position;
                    position = new Point(position.X * scale, position.Y * scale);
                    if (properties.IsLeftButtonPressed)
                    {
                        mLastLeftMousePosition = position;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseButtonDown((float)position.X, (float)position.Y, leftMouseButton);
                        });
                    }
                    if (properties.IsRightButtonPressed)
                    {
                        mLastRightMousePosition = position;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseButtonDown((float)position.X, (float)position.Y, rightMouseButton);
                        });
                    }
                }
            };
            GLView.PointerMoved += (sender, args) =>
            {
                if (args.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse)
                {
                    var properties = args.GetCurrentPoint((UIElement)sender).Properties;
                    var position = args.GetCurrentPoint((UIElement)sender).Position;
                    position = new Point(position.X * scale, position.Y * scale);
                    if (properties.IsLeftButtonPressed && mLastLeftMousePosition != null)
                    {
                        var lastPos = mLastLeftMousePosition;
                        var oldPos = (Point)lastPos;

                        var x = position.X - oldPos.X;
                        var y = position.Y - oldPos.Y;
                        mLastLeftMousePosition = position;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseMove((float)x, (float)y, leftMouseButton);
                        });
                    }
                    if (properties.IsRightButtonPressed && mLastRightMousePosition != null)
                    {
                        var lastPos = mLastRightMousePosition;
                        var oldPos = (Point)lastPos;

                        var x = position.X - oldPos.X;
                        var y = position.Y - oldPos.Y;
                        mLastRightMousePosition = position;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseMove((float)x, (float)y, rightMouseButton);
                        });
                    }
                }
            };
            GLView.PointerReleased += (sender, args) =>
            {
                if (args.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse)
                {
                    var properties = args.GetCurrentPoint((UIElement)sender).Properties;
                    var position = args.GetCurrentPoint((UIElement)sender).Position;
                    position = new Point(position.X * scale, position.Y * scale);
                    if (mLastLeftMousePosition != null && !properties.IsLeftButtonPressed)
                    {
                        mLastLeftMousePosition = null;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseButtonUp((float)position.X, (float)position.Y, leftMouseButton);
                        });
                    }
                    if (mLastRightMousePosition != null && !properties.IsRightButtonPressed)
                    {
                        mLastRightMousePosition = null;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseButtonUp((float)position.X, (float)position.Y, rightMouseButton);
                        });
                    }
                }
            };
            GLView.PointerWheelChanged += (sender, arg) =>
            {
                var delta = arg.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
                mRenderer.EnqueueTask(() =>
                {
                    mAppCore.MouseWheel(delta > 0 ? 1 : -1, 0);
                });
            };
            Window.Current.CoreWindow.CharacterReceived += (sender, arg) =>
            {
                if (OverlayContainer.Content != null) return;
                mRenderer.EnqueueTask(() =>
                {
                    mAppCore.CharEnter((short)arg.KeyCode);
                });
            };
            Window.Current.CoreWindow.KeyDown += (sender, arg) =>
            {
                if (OverlayContainer.Content != null) return;

                var modifiers = 0;
                if (CoreWindow.GetForCurrentThread().GetKeyState(Windows.System.VirtualKey.Control) == CoreVirtualKeyStates.Down)
                    modifiers |= 16;
                if (CoreWindow.GetForCurrentThread().GetKeyState(Windows.System.VirtualKey.Shift) == CoreVirtualKeyStates.Down)
                    modifiers |= 8;

                mRenderer.EnqueueTask(() =>
                {
                    mAppCore.KeyDown((int)arg.VirtualKey, modifiers);
                });
            };
            Window.Current.CoreWindow.KeyUp += (sender, arg) =>
            {
                if (OverlayContainer.Content != null) return;
                mRenderer.EnqueueTask(() =>
                {
                    mAppCore.KeyUp((int)arg.VirtualKey, 0);
                });
            };
        }

        void PopulateMenuBar()
        {
            MenuBar.AllowFocusOnInteraction = false;
            MenuBar.IsFocusEngagementEnabled = false;
            MenuBar.Visibility = Visibility.Visible;
            var fileItem = new MenuBarItem();
            fileItem.Title = LocalizationHelper.Localize("File");

            AppendItem(fileItem, LocalizationHelper.Localize("Open Script..."), (sender, arg) =>
            {
                PickScript();
            });

            var scriptsItem = new MenuFlyoutSubItem();
            scriptsItem.Text = LocalizationHelper.Localize("Scripts");
            var scripts = CelestiaAppCore.ReadScripts(mCurrentPath + "\\scripts", true);
            foreach (var script in scripts)
            {
                AppendSubItem(scriptsItem, script.Title, (sender, arg) =>
                {
                    mAppCore.RunScript(script.Filename);
                });
            }
            if (mExtraScriptFolder != null)
            {
                var extraScripts = CelestiaAppCore.ReadScripts(mExtraScriptFolder, true);
                foreach (var script in extraScripts)
                {
                    AppendSubItem(scriptsItem, script.Title, (sender, arg) =>
                    {
                        mAppCore.RunScript(script.Filename);
                    });
                }
            }
            fileItem.Items.Add(scriptsItem);

            fileItem.Items.Add(new MenuFlyoutSeparator());

            AppendItem(fileItem, LocalizationHelper.Localize("Capture Image"), (sender, arg) =>
            {
                CaptureImage();
            });

            fileItem.Items.Add(new MenuFlyoutSeparator());


            AppendItem(fileItem, LocalizationHelper.Localize("Exit"), (sender, arg) =>
            {
                Application.Current.Exit();
            });

            var navigationItem = new MenuBarItem();
            navigationItem.Title = LocalizationHelper.Localize("Navigation");

            AppendItem(navigationItem, LocalizationHelper.Localize("Select Sol"), (sender, arg) =>
            {
                mAppCore.CharEnter(104);
            });
            AppendItem(navigationItem, LocalizationHelper.Localize("Tour Guide"), (sender, arg) =>
            {
                ShowTourGuide();
            });
            AppendItem(navigationItem, LocalizationHelper.Localize("Select Object"), (sender, arg) =>
            {
                ShowSelectObject();
            });
            AppendItem(navigationItem, LocalizationHelper.Localize("Go to Object"), (sender, arg) =>
            {
                ShowGotoObject();
            });

            navigationItem.Items.Add(new MenuFlyoutSeparator());

            var actions = new (String, short)[] {
                    ("Go", 103),
                    ("Follow", 102),
                    ("Orbit Synchronously", 121),
                    ("Lock Phase", 58),
                    ("Chase", 34),
                    ("Track", 116)
                };
            foreach (var action in actions)
            {
                AppendItem(navigationItem, LocalizationHelper.Localize(action.Item1), (sender, arg) =>
                {
                    mAppCore.CharEnter(action.Item2);
                });
            }
            navigationItem.Items.Add(new MenuFlyoutSeparator());

            AppendItem(navigationItem, LocalizationHelper.Localize("Browser"), (sender, arg) =>
            {
                ShowBrowser();
            });
            AppendItem(navigationItem, LocalizationHelper.Localize("Eclipse Finder"), (sender, arg) =>
            {
                ShowEclipseFinder();
            });

            var timeItem = new MenuBarItem();
            timeItem.Title = LocalizationHelper.Localize("Time");
            AppendItem(timeItem, LocalizationHelper.Localize("10x Faster"), (sender, arg) =>
            {
                mAppCore.CharEnter(108);
            });
            AppendItem(timeItem, LocalizationHelper.Localize("10x Slower"), (sender, arg) =>
            {
                mAppCore.CharEnter(107);
            });
            AppendItem(timeItem, LocalizationHelper.Localize("Freeze"), (sender, arg) =>
            {
                mAppCore.CharEnter(32);
            });
            AppendItem(timeItem, LocalizationHelper.Localize("Real Time"), (sender, arg) =>
            {
                mAppCore.CharEnter(33);
            });
            AppendItem(timeItem, LocalizationHelper.Localize("Reverse Time"), (sender, arg) =>
            {
                mAppCore.CharEnter(106);
            });

            timeItem.Items.Add(new MenuFlyoutSeparator());

            AppendItem(timeItem, LocalizationHelper.Localize("Set Time..."), (sender, arg) =>
            {
                ShowTimeSetting();
            });


            var renderItem = new MenuBarItem();
            renderItem.Title = LocalizationHelper.Localize("Render");

            AppendItem(renderItem, LocalizationHelper.Localize("View Options"), (sender, arg) =>
            {
                ShowViewOptions();
            });
            AppendItem(renderItem, LocalizationHelper.Localize("Locations"), (sender, arg) =>
            {
                ShowLocationSettings();
            });
            renderItem.Items.Add(new MenuFlyoutSeparator());
            AppendItem(renderItem, LocalizationHelper.Localize("More Stars Visible"), (sender, arg) =>
            {
                mAppCore.CharEnter(93);
            });
            AppendItem(renderItem, LocalizationHelper.Localize("Fewer Stars Visible"), (sender, arg) =>
            {
                mAppCore.CharEnter(91);
            });
            var starStyleItem = new MenuFlyoutSubItem();
            starStyleItem.Text = LocalizationHelper.Localize("Star Style");
            AppendSubItem(starStyleItem, LocalizationHelper.Localize("Fuzzy Points"), (sender, arg) =>
            {
                mAppCore.StarStyle = 0;
            });
            AppendSubItem(starStyleItem, LocalizationHelper.Localize("Points"), (sender, arg) =>
            {
                mAppCore.StarStyle = 1;
            });
            AppendSubItem(starStyleItem, LocalizationHelper.Localize("Scaled Discs"), (sender, arg) =>
            {
                mAppCore.StarStyle = 2;
            });
            renderItem.Items.Add(starStyleItem);
            renderItem.Items.Add(new MenuFlyoutSeparator());
            var resolutionItem = new MenuFlyoutSubItem();
            resolutionItem.Text = LocalizationHelper.Localize("Texture Resolution");
            AppendSubItem(resolutionItem, LocalizationHelper.Localize("Low"), (sender, arg) =>
            {
                mAppCore.Resolution = 0;
            });
            AppendSubItem(resolutionItem, LocalizationHelper.Localize("Medium"), (sender, arg) =>
            {
                mAppCore.Resolution = 1;
            });
            AppendSubItem(resolutionItem, LocalizationHelper.Localize("High"), (sender, arg) =>
            {
                mAppCore.Resolution = 2;
            });
            renderItem.Items.Add(resolutionItem);

            var viewItem = new MenuBarItem();
            viewItem.Title = LocalizationHelper.Localize("View");
            AppendItem(viewItem, LocalizationHelper.Localize("Split Horizontally"), (sender, arg) =>
            {
                mAppCore.CharEnter(18);
            });
            AppendItem(viewItem, LocalizationHelper.Localize("Split Vertically"), (sender, arg) =>
            {
                mAppCore.CharEnter(21);
            });
            AppendItem(viewItem, LocalizationHelper.Localize("Delete Active View"), (sender, arg) =>
            {
                mAppCore.CharEnter(127);
            });
            AppendItem(viewItem, LocalizationHelper.Localize("Single View"), (sender, arg) =>
            {
                mAppCore.CharEnter(4);
            });

            var bookmarkItem = new MenuBarItem();
            bookmarkItem.Title = LocalizationHelper.Localize("Bookmarks");
            AppendItem(bookmarkItem, LocalizationHelper.Localize("Add Bookmarks"), (sender, arg) =>
            {
                ShowNewBookmark();
            });
            AppendItem(bookmarkItem, LocalizationHelper.Localize("Organize Bookmarks"), (sender, arg) =>
            {
                ShowBookmarkOrganizer();
            });

            var helpItem = new MenuBarItem();
            helpItem.Title = LocalizationHelper.Localize("Help");
            AppendItem(helpItem, LocalizationHelper.Localize("Run Demo"), (sender, arg) =>
            {
                mAppCore.CharEnter(100);
            });
            helpItem.Items.Add(new MenuFlyoutSeparator());
            AppendItem(helpItem, LocalizationHelper.Localize("OpenGL Info"), (sender, arg) =>
            {
                ShowOpenGLInfo();
            });
            helpItem.Items.Add(new MenuFlyoutSeparator());
            AppendItem(helpItem, LocalizationHelper.Localize("Add-ons"), (sender, arg) =>
            {
                ShowAddons();
            });

            MenuBar.Items.Add(fileItem);
            MenuBar.Items.Add(navigationItem);
            MenuBar.Items.Add(timeItem);
            MenuBar.Items.Add(renderItem);
            MenuBar.Items.Add(viewItem);
            MenuBar.Items.Add(bookmarkItem);
            MenuBar.Items.Add(helpItem);
        }

        void AppendItem(MenuBarItem parent, String text, RoutedEventHandler click)
        {
            var item = new MenuFlyoutItem();
            item.Text = text;
            item.Click += click;
            parent.Items.Add(item);
        }

        void AppendSubItem(MenuFlyoutSubItem parent, String text, RoutedEventHandler click)
        {
            var item = new MenuFlyoutItem();
            item.Text = text;
            item.Click += click;
            parent.Items.Add(item);
        }

        async void PickScript()
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.FileTypeFilter.Add(".cel");
            picker.FileTypeFilter.Add(".celx");
            var file = await picker.PickSingleFileAsync();
            if (file != null)
                mAppCore.RunScript(file.Path);
        }
        void ShowTourGuide()
        {
            ShowPage(typeof(TourGuidePage), new Size(400, 0), mAppCore);
        }
        async void ShowSelectObject()
        {
            var dialog = new TextInputDialog(LocalizationHelper.Localize("Object name:"));
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var text = dialog.Text;
                var selection = mAppCore.Simulation.Find(text);
                if (selection.IsEmpty)
                {
                    ShowObjectNotFound();
                }
                else
                {
                    mAppCore.Simulation.Selection = selection;
                }
            }
        }
        async void ShowGotoObject()
        {
            var dialog = new GotoObjectDialog();
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var objectName = dialog.Text;
                var latitude = dialog.Latitude;
                var longitude = dialog.Longitude;
                var distance = dialog.Distance;
                var unit = (CelestiaGotoLocationDistanceUnit)dialog.Unit;
                var selection = mAppCore.Simulation.Find(objectName);
                if (selection.IsEmpty)
                {
                    ShowObjectNotFound();
                }
                else
                {
                    var location = new CelestiaGotoLocation(selection, latitude, longitude, distance, unit);
                    mAppCore.Simulation.GoToLocation(location);
                }
            }
        }

        void ShowObjectNotFound()
        {
            ContentDialogHelper.ShowAlert(this, LocalizationHelper.Localize("Object not found."));
        }

        void ShowBrowser()
        {
            ShowPage(typeof(BrowserPage), new Size(500, 0), mAppCore);
        }

        void ShowEclipseFinder()
        {
            ShowPage(typeof(EclipseFinderPage), new Size(400, 0), mAppCore);
        }

        async void ShowTimeSetting()
        {
            var dialog = new TimeSettingDialog(mAppCore.Simulation.Time);
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var date = dialog.DisplayDate;
                mAppCore.Simulation.Time = date;
            }
        }

        void ShowViewOptions()
        {
            ShowPage(typeof(ViewOptionsPage), new Size(500, 670), mAppCore);
        }

        void ShowLocationSettings()
        {
            ShowPage(typeof(LocationSettingsPage), new Size(400, 350), mAppCore);
        }

        void ShowPage(Type pageType, Size size, object parameter)
        {
            OverlayBackground.Visibility = Visibility.Visible;
            OverlayContainer.PointerPressed += (sender, arg) =>
            {
                arg.Handled = true;
            };
            OverlayBackground.PointerPressed += (sender, arg) =>
            {
                OverlayBackground.Visibility = Visibility.Collapsed;
                OverlayContainer.Content = null;
            };
            OverlayContainer.Width = size.Width;
            if (size.Height <= 1)
            {
                OverlayContainer.Height = OverlayBackground.Height;
                RelativePanel.SetAlignBottomWithPanel(OverlayContainer, true);
            }
            else
            {
                OverlayContainer.Height = size.Height;
                RelativePanel.SetAlignBottomWithPanel(OverlayContainer, false);
            }
            OverlayContainer.Navigate(pageType, parameter);
        }

        void ShowBookmarkOrganizer()
        {
            ShowPage(typeof(BookmarkOrganizerPage), new Size(400, 0), mAppCore);
        }

        void ShowNewBookmark()
        {
            ShowPage(typeof(NewBookmarkPage), new Size(400, 0), mAppCore);
        }
        async void ShowOpenGLInfo()
        {
            var dialog = new InfoDialog(mAppCore.RenderInfo);
            dialog.Title = LocalizationHelper.Localize("OpenGL Info");
            await dialog.ShowAsync();
        }

        void ShowAddons()
        {
            ShowPage(typeof(Addon.ResourceManagerPage), new Size(450, 0), mExtraAddonFolder);
        }

        async System.Threading.Tasks.Task<string> GetLocale()
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(mLocalePath);
            var files = await folder.GetFoldersAsync();
            var availableLocales = new List<string>();
            var preferredLocale = System.Globalization.CultureInfo.CurrentCulture.Name;
            preferredLocale = preferredLocale.Replace("-", "_");
            foreach (var file in files)
            {
                availableLocales.Add(file.Name);
            }
            if (availableLocales.Contains(preferredLocale))
                return preferredLocale;
            var components = new List<string>(preferredLocale.Split("_"));
            if (components.Count() == 3)
                components.RemoveAt(1);
            if (components.Count() == 2)
                components[1] = components[1].ToUpper();
            preferredLocale = string.Join("_", components);
            if (availableLocales.Contains(preferredLocale))
                return preferredLocale;

            foreach (var lang in availableLocales)
            {
                if (lang == components[0] || lang.StartsWith(components[0] + "_"))
                    return lang;
            }

            return "";
        }

        private async System.Threading.Tasks.Task<Dictionary<string, object>> ReadSettings()
        {
            var installedFolder = Windows.ApplicationModel.Package.Current.InstalledLocation;
            var settingsObject = new Dictionary<string, object>();
            try
            {
                var serializer = new JsonSerializer();
                using (var st = await installedFolder.OpenStreamForReadAsync("defaults.json"))
                using (var sr = new StreamReader(st))
                using (var jsonTextReader = new JsonTextReader(sr))
                {
                    settingsObject = serializer.Deserialize<Dictionary<string, object>>(jsonTextReader);
                }
            }
            catch { }
            var newSettings = new Dictionary<string, object>();
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            foreach (var kvp in settingsObject)
            {
                var propInfo = typeof(CelestiaAppCore).GetProperty(kvp.Key);
                if (propInfo == null) continue;

                var def = kvp.Value;
                var saved = localSettings.Values[kvp.Key];
                if (saved != null)
                {
                    newSettings[kvp.Key] = saved;
                }
                else if (propInfo.PropertyType == typeof(Boolean))
                {
                    newSettings[kvp.Key] = (long)def != 0;
                }
                else if (propInfo.PropertyType == typeof(Double) || propInfo.PropertyType == typeof(Single))
                {
                    newSettings[kvp.Key] = (float)((double)def);
                }
                else if (propInfo.PropertyType == typeof(Int64) || propInfo.PropertyType == typeof(Int32) || propInfo.PropertyType == typeof(Int16))
                {
                    newSettings[kvp.Key] = (int)((long)def);
                }
            }
            return newSettings;
        }

        private Dictionary<string, object> GetCurrentSettings()
        {
            var newSettings = new Dictionary<string, object>();
            foreach (var kvp in mSettings)
            {
                var propInfo = typeof(CelestiaAppCore).GetProperty(kvp.Key);
                if (propInfo == null) continue;

                newSettings[kvp.Key] = mAppCore.GetType().GetProperty(kvp.Key).GetValue(mAppCore);
            }
            return newSettings;
        }

        private void ApplySettings(Dictionary<string, object> settings)
        {
            foreach (var kvp in settings)
            {
                mAppCore.GetType().GetProperty(kvp.Key).SetValue(mAppCore, kvp.Value);
            }
        }

        private void SaveSettings(Dictionary<string, object> settings)
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            foreach (var kvp in settings)
            {
                localSettings.Values[kvp.Key] = kvp.Value;
            }
        }

        private MenuFlyoutItemBase CreateMenuItem(CelestiaBrowserItem item)
        {
            var children = new List<MenuFlyoutItemBase>();
            var obj = item.Object;
            if (obj != null)
            {
                var selectItem = new MenuFlyoutItem();
                selectItem.Text = LocalizationHelper.Localize("Select");
                selectItem.Click += (sender, arg) =>
                {
                    mAppCore.Simulation.Selection = new CelestiaSelection(obj);
                };
                children.Add(selectItem);
            }
            if (item.Children != null)
            {
                if (children.Count > 0)
                    children.Add(new MenuFlyoutSeparator());

                foreach (var child in item.Children)
                {
                    children.Add(CreateMenuItem(child));
                }
            }
            if (children.Count == 0)
            {
                var menuItem = new MenuFlyoutItem();
                menuItem.Text = item.Name;
                return menuItem;
            }
            var menu = new MenuFlyoutSubItem();
            menu.Text = item.Name;
            foreach (var child in children)
            {
                menu.Items.Add(child);
            }
            return menu;
        }
        private CelestiaBrowserItem[] GetChildren(CelestiaBrowserItem item)
        {
            var obj = item.Object;
            if (obj == null)
                return new CelestiaBrowserItem[] { };
            if (obj is CelestiaStar)
                return mAppCore.Simulation.Universe.ChildrenForStar((CelestiaStar)obj, GetChildren);
            if (obj is CelestiaBody)
                return mAppCore.Simulation.Universe.ChildrenForBody((CelestiaBody)obj, GetChildren);
            return new CelestiaBrowserItem[] { };
        }

        private void CaptureImage()
        {
            var tempFolder = Windows.Storage.ApplicationData.Current.TemporaryFolder;
            var path = tempFolder.Path + "\\" + GuidHelper.CreateNewGuid().ToString() + ".png";
            mRenderer.EnqueueTask(() =>
            {
                mAppCore.Draw();
                if (mAppCore.SaveScreenshot(path))
                {
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                      {
                          SaveScreenshot(path);
                      });
                }
                else
                {
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        ShowScreenshotFailure();
                    });
                }
            });
        }

        private void ShowScreenshotFailure()
        {
            ContentDialogHelper.ShowAlert(this, LocalizationHelper.Localize("Failed in capturing screenshot."));
        }

        private async void SaveScreenshot(string path)
        {
            try
            {
                var originalFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                if (originalFile == null) return;

                var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                savePicker.SuggestedStartLocation =
                    Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                // Dropdown of file types the user can save the file as
                savePicker.FileTypeChoices.Add(LocalizationHelper.Localize("Image"), new List<string>() { ".png" });
                // Default file name if the user does not type one in or select a file to replace
                savePicker.SuggestedFileName = LocalizationHelper.Localize("Celestia Screenshot");
                Windows.Storage.StorageFile file = await savePicker.PickSaveFileAsync();
                if (file == null) return;

                await originalFile.CopyAndReplaceAsync(file);
            }
            catch
            {
                ShowScreenshotFailure();
            }
        }
    }
}
