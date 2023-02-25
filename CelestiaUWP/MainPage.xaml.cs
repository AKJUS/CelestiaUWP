//
// MainPage.xaml.cs
//
// Copyright © 2021 Celestia Development Team. All rights reserved.
//
// This program is free software, you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 2
// of the License, or (at your option) any later version.
//

using CelestiaComponent;
using CelestiaUWP.Helper;
using CelestiaUWP.Web;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.Resources;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

using MenuBarItem = Microsoft.UI.Xaml.Controls.MenuBarItem;

namespace CelestiaUWP
{
    public sealed partial class MainPage : Page
    {
        private readonly CelestiaAppCore mAppCore;
        private CelestiaRenderer mRenderer;

        private Point? mLastLeftMousePosition = null;
        private Point? mLastRightMousePosition = null;
        private Point? mLastMiddleMousePosition = null;

        private string mExtraAddonFolder;
        private string mExtraScriptFolder;

        private Windows.Storage.StorageFile ScriptFileToOpen;
        private Uri URLToOpen;
        private bool ReadyForInput = false;

        private readonly AppSettings AppSettings = AppSettings.Shared;

        private bool isXbox = false;
        // Used in renderer thread
        private bool isGLViewFocused = false;
        private bool hasOverlayOpen = false;

        private string defaultParentPath
        {
            get { return Windows.ApplicationModel.Package.Current.InstalledLocation.Path; }
        }

        private string defaultResourcePath
        {
            get { return defaultParentPath + "\\CelestiaResources"; }
        }

        private string defaultConfigFilePath
        {
            get { return defaultResourcePath + "\\celestia.cfg"; }
        }

        private string[] AvailableLanguages;

        private readonly string[] Markers = new string[]
        {
            "Diamond", "Triangle", "Filled Square", "Plus", "X", "Left Arrow", "Right Arrow", "Up Arrow", "Down Arrow",
            "Circle", "Disk", "Crosshair"
        };

        private float scale = 1.0f;

        private GamepadManager gamepadManager = null;

        public MainPage()
        {
            CelestiaAppCore.SetUpLocale();

            mAppCore = new CelestiaAppCore();

            InitializeComponent();

            Loaded += MainPage_Loaded;
            SizeChanged += MainPage_SizeChanged;

            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.ExtendViewIntoTitleBar = false;
            isXbox = Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";
        }

        private void MainPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!ReadyForInput || isXbox) return;

            var isFullScreen = ApplicationView.GetForCurrentView().IsFullScreenMode;
            MenuBar.Visibility = isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            scale = AppSettings.UseFullDPI ? ((int)Windows.Graphics.Display.DisplayInformation.GetForCurrentView().ResolutionScale) / 100.0f : 1.0f;

            Windows.Storage.StorageFolder customDataFolder = null;
            Windows.Storage.StorageFile customConfigFile = null;
            try
            {
                customDataFolder = await Windows.Storage.ApplicationData.Current.LocalFolder.GetFolderAsync("Override");
                customConfigFile = await customDataFolder.GetFileAsync("celestia.cfg");
            }
            catch { }

            var resourcePath = customDataFolder != null ? customDataFolder.Path : defaultResourcePath;
            var configPath = customConfigFile != null ? customConfigFile.Path : defaultConfigFilePath;

            var localePath = defaultResourcePath + "\\locale";
            var systemLocale = await GetLocale(localePath);
            var locale = AppSettings.LanguageOverride;
            if (locale == null)
                locale = systemLocale;

            await Task.Run(() => CreateExtraFolders());

            mRenderer = new CelestiaRenderer(AppSettings.EnableMSAA, () => {
                CelestiaAppCore.InitGL();

                List<string> extraPaths = new List<string>();
                if (mExtraAddonFolder != null)
                    extraPaths.Add(mExtraAddonFolder);

                void progressCallback(string progress)
                {
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        LoadingText.Text = string.Format(LocalizationHelper.Localize("Loading: %s").Replace("%s", "{0}"), progress);
                    });
                }

                Directory.SetCurrentDirectory(resourcePath);
                CelestiaAppCore.SetLocaleDirectory(resourcePath + "\\locale", locale);
                if (!mAppCore.StartSimulation(configPath, extraPaths.ToArray(), progressCallback) && (resourcePath != defaultResourcePath || configPath != defaultConfigFilePath))
                {
                    if (resourcePath != defaultResourcePath || configPath != defaultConfigFilePath)
                    {
                        // Try to restore originial settings
                        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            ContentDialogHelper.ShowAlert(this, LocalizationHelper.Localize("Error loading data, fallback to original configuration."));
                        });
                        Directory.SetCurrentDirectory(defaultResourcePath);
                        CelestiaAppCore.SetLocaleDirectory(defaultResourcePath + "\\locale", locale);
                        if (!mAppCore.StartSimulation(defaultConfigFilePath, extraPaths.ToArray(), progressCallback))
                        {
                            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                            {
                                ShowLoadingFailure();
                            });
                            return false;
                        }
                    }
                    else
                    {
                        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            ShowLoadingFailure();
                        });
                        return false;
                    }
                }

                if (!mAppCore.StartRenderer())
                {
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        ShowLoadingFailure();
                    });
                    return false;
                }

                LocalizationHelper.Locale = CelestiaAppCore.Language;
                UpdateScale();

                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    LoadingText.Visibility = Visibility.Collapsed;
                    Addon.ResourceManager.Shared.AddonFolderPath = mExtraAddonFolder;
                    SetUpGLViewInteractions();
                    PopulateMenuBar(resourcePath);
                });

                ApplySettings(ReadDefaultSettings().Result);

                mAppCore.Start();

                ReadyForInput = true;

                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    OpenFileOrURL();
                });

                return true;
            });
            mRenderer.SetCorePointer(mAppCore.Pointer);
            mRenderer.SetSurface(GLView, scale);
            mRenderer.Start();
        }

        private void UpdateScale()
        {
            mAppCore.SetDPI((int)(96 * scale));
            var fontMap = new Dictionary<string, (string, int, string, int)>()
                {
                    { "ja", ("NotoSansCJK-Regular.ttc", 0, "NotoSansCJK-Bold.ttc", 0) },
                    { "ko", ("NotoSansCJK-Regular.ttc", 1, "NotoSansCJK-Bold.ttc", 1) },
                    { "zh_CN", ("NotoSansCJK-Regular.ttc", 2, "NotoSansCJK-Bold.ttc", 2) },
                    { "zh_TW", ("NotoSansCJK-Regular.ttc", 3, "NotoSansCJK-Bold.ttc", 3) },
                    { "ar", ("NotoSansArabic-Regular.ttf", 0, "NotoSansArabic-Bold.ttf", 0) },
                };
            var defaultFont = ("NotoSans-Regular.ttf", 0, "NotoSans-Bold.ttf", 0);
            var font = fontMap.GetValueOrDefault(LocalizationHelper.Locale, defaultFont);

            var pathPrefix = defaultResourcePath + "\\fonts\\";
            mAppCore.ClearFonts();
            mAppCore.SetFont(pathPrefix + font.Item1, font.Item2, 9);
            mAppCore.SetTitleFont(pathPrefix + font.Item3, font.Item4, 15);
            mAppCore.SetRenderFont(pathPrefix + font.Item1, font.Item2, 9, CelestiaFontStyle.Normal);
            mAppCore.SetRenderFont(pathPrefix + font.Item3, font.Item4, 15, CelestiaFontStyle.Large);
        }

        private void ShowLoadingFailure()
        {
            LoadingText.Text = LocalizationHelper.Localize("Loading Celestia failed…");
        }

        internal class GuideItem
        {
            public string id;
            public string title;
        }

        private async void OpenFileOrURL()
        {
            var scriptFile = ScriptFileToOpen;
            var url = URLToOpen;
            if (scriptFile != null)
            {
                ScriptFileToOpen = null;
                var fileExtension = scriptFile.FileType;
                var tempFolder = Windows.Storage.ApplicationData.Current.TemporaryFolder;
                try
                {
                    var copiedFile = await scriptFile.CopyAsync(tempFolder, GuidHelper.CreateNewGuid().ToString() + fileExtension, Windows.Storage.NameCollisionOption.ReplaceExisting);
                    if (await ContentDialogHelper.ShowOption(this, LocalizationHelper.Localize("Run script?")))
                    {
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.RunScript(copiedFile.Path);
                        });
                    }
                }
                catch { }
                return;
            }
            if (url != null)
            {
                URLToOpen = null;
                if (!isXbox && url.Scheme == "celaddon" && url.Host == "item" && url.Query != null)
                {
                    var query = System.Web.HttpUtility.ParseQueryString(url.Query);
                    var addon = query["item"];
                    if (addon == null) { return; }

                    Windows.Web.Http.HttpClient httpClient = new Windows.Web.Http.HttpClient();
                    var queryItems = System.Web.HttpUtility.ParseQueryString("");
                    queryItems.Add("lang", LocalizationHelper.Locale);
                    queryItems.Add("item", addon);
                    var builder = new UriBuilder(Addon.Constants.APIPrefix + "/resource/item");
                    builder.Query = queryItems.ToString();
                    try
                    {
                        var httpResponse = await httpClient.GetAsync(builder.Uri);
                        httpResponse.EnsureSuccessStatusCode();
                        var httpResponseBody = await httpResponse.Content.ReadAsStringAsync();
                        var requestResult = JsonConvert.DeserializeObject<Addon.RequestResult>(httpResponseBody);
                        if (requestResult.status != 0) return;
                        var item = requestResult.Get<Addon.ResourceItem>();
                        ShowPage(typeof(Addon.ResourceItemPage), new Size(450, 0),new Addon.AddonPageParameter(mAppCore, mRenderer, item));
                    }
                    catch { }
                    return;
                }
                else if (!isXbox && url.Scheme == "celguide" && url.Host == "guide" && url.Query != null)
                {
                    var query = System.Web.HttpUtility.ParseQueryString(url.Query);
                    var guide = query["guide"];
                    if (guide == null) return;
                    ShowPage(typeof(SafeWebPage), new Size(450, 0), GenerateWebArgsForGuide(guide));
                    return;
                }
                else if (url.Scheme == "cel")
                {
                    if (await ContentDialogHelper.ShowOption(this, LocalizationHelper.Localize("Open URL?")))
                    {
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.GoToURL(url.AbsoluteUri);
                        });
                    }
                    return;
                }
            }
            if (!isXbox)
            {
                Windows.Web.Http.HttpClient httpClient = new Windows.Web.Http.HttpClient();
                var queryItems = System.Web.HttpUtility.ParseQueryString("");
                queryItems.Add("lang", LocalizationHelper.Locale);
                queryItems.Add("type", "news");
                var builder = new UriBuilder(Addon.Constants.APIPrefix + "/resource/latest");
                builder.Query = queryItems.ToString();

                try
                {
                    var httpResponse = await httpClient.GetAsync(builder.Uri);
                    httpResponse.EnsureSuccessStatusCode();
                    var httpResponseBody = await httpResponse.Content.ReadAsStringAsync();
                    var requestResult = JsonConvert.DeserializeObject<Addon.RequestResult>(httpResponseBody);
                    if (requestResult.status != 0) return;
                    var item = requestResult.Get<GuideItem>();
                    var appSettings = AppSettings;
                    if (item.id == null || appSettings.LastNewsID == item.id) return;
                    var args = GenerateWebArgsForGuide(item.id);
                    args.ACKReceiver = (id) =>
                    {
                        if (id == item.id)
                        {
                            appSettings.LastNewsID = id;
                            appSettings.Save();
                        }
                    };
                    ShowPage(typeof(SafeWebPage), new Size(450, 0), args);
                }
                catch { }
            }
        }

        public CommonWebArgs GenerateWebArgsForGuide(string id)
        {
            var queryItems = System.Web.HttpUtility.ParseQueryString("");
            queryItems.Add("lang", LocalizationHelper.Locale);
            queryItems.Add("guide", id);
            queryItems.Add("platform", "uwp");
            queryItems.Add("transparentBackground", "1");
            var builder = new UriBuilder("https://celestia.mobi/resources/guide");
            builder.Query = queryItems.ToString();
            var args = new CommonWebArgs();
            args.Renderer = mRenderer;
            args.AppCore = mAppCore;
            args.Uri = builder.Uri;
            args.MatchingQueryKeys = new string[] { "guide" };
            return args;
        }

        public void OpenFileIfReady(Windows.Storage.StorageFile scriptFileToOpen)
        {
            ScriptFileToOpen = scriptFileToOpen;
            if (ReadyForInput)
                OpenFileOrURL();
        }

        public void OpenURLIfReady(Uri URL)
        {
            URLToOpen = URL;
            if (ReadyForInput)
                OpenFileOrURL();
        }

        private async void CreateExtraFolders()
        {
            var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
            try
            {
                var mainFolder = await folder.CreateFolderAsync("CelestiaResources", Windows.Storage.CreationCollisionOption.OpenIfExists);
                var addonFolder = await mainFolder.CreateFolderAsync("extras", Windows.Storage.CreationCollisionOption.OpenIfExists);
                mExtraAddonFolder = addonFolder.Path;
                var scriptFolder = await mainFolder.CreateFolderAsync("scripts", Windows.Storage.CreationCollisionOption.OpenIfExists);
                mExtraScriptFolder = scriptFolder.Path;
            } catch { }
        }

        void SetUpGLViewInteractions()
        {
            mAppCore.ShowContextMenu += (_, contextMenuArgs) =>
            {
                var x = contextMenuArgs.X;
                var y = contextMenuArgs.Y;
                var selection = contextMenuArgs.Selection;
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                  {
                      var menu = new MenuFlyout();
                      AppendItem(menu, mAppCore.Simulation.Universe.NameForSelection(selection), null);
                      menu.Items.Add(new MenuFlyoutSeparator());
                      var actions = new (string, short)[]
                      {
                        ("Go", 103),
                        ("Follow", 102),
                        ("Sync Orbit", 121),
                      };

                      foreach (var action in actions)
                      {
                          AppendItem(menu, LocalizationHelper.Localize(action.Item1), (sender, arg) =>
                          {
                              mRenderer.EnqueueTask(() =>
                              {
                                  mAppCore.Simulation.Selection = selection;
                                  mAppCore.CharEnter(action.Item2);
                              });
                          });
                      }

                      if (selection.Object is CelestiaBody body)
                      {
                          var surfaces = body.AlternateSurfaceNames;
                          if (surfaces != null && surfaces.Length > 0)
                          {
                              menu.Items.Add(new MenuFlyoutSeparator());

                              var altSur = new MenuFlyoutSubItem
                              {
                                  Text = LocalizationHelper.Localize("Alternate Surfaces")
                              };
                              AppendSubItem(altSur, LocalizationHelper.Localize("Default"), (sender, arg) =>
                              {
                                  mRenderer.EnqueueTask(() =>
                                  {
                                      mAppCore.Simulation.ActiveObserver.DisplayedSurfaceName = "";
                                  });
                              });

                              foreach (var name in surfaces)
                              {
                                  AppendSubItem(altSur, name, (sender, arg) =>
                                  {
                                      mRenderer.EnqueueTask(() =>
                                      {
                                          mAppCore.Simulation.ActiveObserver.DisplayedSurfaceName = name;
                                      });
                                  });
                              }

                              menu.Items.Add(altSur);
                          }

                          menu.Items.Add(new MenuFlyoutSeparator());
                          var refMarkMenu = new MenuFlyoutSubItem
                          {
                              Text = LocalizationHelper.Localize("Reference Vectors")
                          };

                          var refMarkMenuActions = new (string, string)[]
                          {
                              ("Show Body Axes", "ShowBodyAxes"),
                              ("Show Frame Axes", "ShowFrameAxes"),
                              ("Show Sun Direction", "ShowSunDirection"),
                              ("Show Velocity Vector", "ShowVelocityVector"),
                              ("Show Planetographic Grid", "ShowPlanetographicGrid"),
                              ("Show Terminator", "ShowTerminator")
                          };

                          foreach (var action in refMarkMenuActions)
                          {
                              var isEnabled = (bool)mAppCore.GetType().GetProperty(action.Item2).GetValue(mAppCore);
                              AppendToggleSubItem(refMarkMenu, LocalizationHelper.Localize(action.Item1), isEnabled, (sender, args) =>
                              {
                                  mRenderer.EnqueueTask(() =>
                                  {
                                      mAppCore.GetType().GetProperty(action.Item2).SetValue(mAppCore, !isEnabled);
                                  });
                              });
                          }
                          menu.Items.Add(refMarkMenu);
                      }

                      var browserMenuItems = new List<MenuFlyoutItemBase>();
                      var browserItem = new CelestiaBrowserItem(mAppCore.Simulation.Universe.NameForSelection(selection), selection.Object, (CelestiaBrowserItem item) => { return CelestiaExtension.GetChildren(item, mAppCore); }, false);
                      if (browserItem.Children != null)
                      {
                          foreach (var child in browserItem.Children)
                          {
                              browserMenuItems.Add(CreateMenuItem(child));
                          }
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
                      AppendItem(menu, LocalizationHelper.Localize("Show Info"), (sender, arg) =>
                      {
                          ShowInfo(selection);
                      });

                      menu.Items.Add(new MenuFlyoutSeparator());

                      if (mAppCore.Simulation.Universe.IsSelectionMarked(selection))
                      {
                          AppendItem(menu, LocalizationHelper.Localize("Unmark"), (sender, arg) =>
                          {
                              mRenderer.EnqueueTask(() =>
                              {
                                  mAppCore.Simulation.Universe.UnmarkSelection(selection);
                              });
                          });
                      }
                      else
                      {
                          var action = new MenuFlyoutSubItem
                          {
                              Text = LocalizationHelper.Localize("Mark")
                          };
                          for (int i = 0; i < Markers.Length; i += 1)
                          {
                              int copy = i;
                              AppendSubItem(action, LocalizationHelper.Localize(Markers[i]), (sender, arg) =>
                              {
                                  mRenderer.EnqueueTask(() =>
                                  {
                                      mAppCore.Simulation.Universe.MarkSelection(selection, (CelestiaMarkerRepresentation)copy);
                                      mAppCore.ShowMarkers = true;
                                  });
                              });
                          }
                          menu.Items.Add(action);
                      }
                      menu.ShowAt(GLView, new Point(x / scale, y / scale));
                  });
            };
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
                            mAppCore.MouseButtonDown((float)position.X, (float)position.Y, CelestiaMouseButton.Left);
                        });
                    }
                    if (properties.IsRightButtonPressed)
                    {
                        mLastRightMousePosition = position;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseButtonDown((float)position.X, (float)position.Y, CelestiaMouseButton.Right);
                        });
                    }
                    if (properties.IsMiddleButtonPressed)
                    {
                        mLastMiddleMousePosition = position;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseButtonDown((float)position.X, (float)position.Y, CelestiaMouseButton.Middle);
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
                            mAppCore.MouseMove((float)x, (float)y, CelestiaMouseButton.Left);
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
                            mAppCore.MouseMove((float)x, (float)y, CelestiaMouseButton.Right);
                        });
                    }
                    if (properties.IsMiddleButtonPressed && mLastMiddleMousePosition != null)
                    {
                        var lastPos = mLastMiddleMousePosition;
                        var oldPos = (Point)lastPos;

                        var x = position.X - oldPos.X;
                        var y = position.Y - oldPos.Y;
                        mLastMiddleMousePosition = position;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseMove((float)x, (float)y, CelestiaMouseButton.Middle);
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
                            mAppCore.MouseButtonUp((float)position.X, (float)position.Y, CelestiaMouseButton.Left);
                        });
                    }
                    if (mLastRightMousePosition != null && !properties.IsRightButtonPressed)
                    {
                        mLastRightMousePosition = null;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseButtonUp((float)position.X, (float)position.Y, CelestiaMouseButton.Right);
                        });
                    }
                    if (mLastMiddleMousePosition != null && !properties.IsMiddleButtonPressed)
                    {
                        mLastMiddleMousePosition = null;
                        mRenderer.EnqueueTask(() =>
                        {
                            mAppCore.MouseButtonUp((float)position.X, (float)position.Y, CelestiaMouseButton.Middle);
                        });
                    }
                }
            };
            GLView.PointerWheelChanged += (sender, arg) =>
            {
                var delta = arg.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
                mRenderer.EnqueueTask(() =>
                {
                    mAppCore.MouseWheel(delta > 0 ? -1 : 1, 0);
                });
            };
            FocusHelperControl.Focus(FocusState.Programmatic);
            isGLViewFocused = true;
            FocusManager.GotFocus += FocusManager_GotFocus;
            FocusHelperControl.CharacterReceived += (sender, arg) =>
            {
                if (OverlayContainer.Content != null) return;
                short key = (short)arg.Character;

                mRenderer.EnqueueTask(() =>
                {
                    mAppCore.CharEnter(key);
                });
            };
            FocusHelperControl.KeyDown += (sender, arg) =>
            {
                if (OverlayContainer.Content != null) return;
                // Gamepad buttons, ignore
                if (arg.OriginalKey >= VirtualKey.GamepadA && arg.OriginalKey <= VirtualKey.GamepadRightThumbstickLeft) return;

                var modifiers = 0;
                if (CoreWindow.GetForCurrentThread().GetKeyState(VirtualKey.Control) == CoreVirtualKeyStates.Down)
                    modifiers |= 16;
                if (CoreWindow.GetForCurrentThread().GetKeyState(VirtualKey.Shift) == CoreVirtualKeyStates.Down)
                    modifiers |= 8;
                int key = (int)arg.Key;

                mRenderer.EnqueueTask(() =>
                {
                    mAppCore.KeyDown(key, modifiers);
                });
            };
            FocusHelperControl.KeyUp += FocusHelperControl_KeyUp;
            MenuBar.KeyUp += (_, args) =>
            {
                if (isXbox && args.Key == VirtualKey.GamepadMenu && OverlayContainer.Content == null)
                {
                    MenuBar.Visibility = Visibility.Collapsed;
                    FocusHelperControl.Focus(FocusState.Programmatic);
                }
            };
            gamepadManager = new GamepadManager();
            mRenderer.SetPreRenderTask(() =>
            {
                if (!isGLViewFocused || hasOverlayOpen) return;
                gamepadManager.PollGamepad(mAppCore);
            });
            SystemNavigationManager.GetForCurrentView().BackRequested += (sender, args) =>
            {
                if (OverlayContainer.Content != null)
                {
                    args.Handled = true;
                    ClosePanel();
                    return;
                }
                if (MenuBar.Visibility == Visibility.Visible)
                {
                    args.Handled = true;
                    MenuBar.Visibility = Visibility.Collapsed;
                    FocusHelperControl.Focus(FocusState.Programmatic);
                    return;
                }
                args.Handled = true;
            };
        }

        private void FocusHelperControl_KeyUp(object sender, KeyRoutedEventArgs args)
        {
            if (OverlayContainer.Content != null) return;
            if (isXbox)
            {
                if (args.Key == VirtualKey.GamepadMenu)
                {
                    // Show menu
                    MenuBar.Visibility = Visibility.Visible;
                    if (MenuBar.Items[0].IsLoaded)
                        MenuBar.Items[0].Focus(FocusState.Programmatic);
                    else
                        MenuBar.Items[0].Loaded += (s, a) =>
                        {
                            ((MenuBarItem)s).Focus(FocusState.Programmatic);
                        };
                    return;
                }
            }

            // Gamepad buttons, ignore
            if (args.OriginalKey >= VirtualKey.GamepadA && args.OriginalKey <= VirtualKey.GamepadRightThumbstickLeft) return;

            int key = (int)args.Key;

            mRenderer.EnqueueTask(() =>
            {
                mAppCore.KeyUp(key, 0);
            });
        }

        private void FocusManager_GotFocus(object sender, FocusManagerGotFocusEventArgs e)
        {
            var focusedItem = e.NewFocusedElement;
            var glViewHasFocus = focusedItem == FocusHelperControl;
            if (!glViewHasFocus)
            {
                var element = focusedItem as FrameworkElement;
                if (element != null)
                {
                    // Do not give up focus to GLView and its parents
                    FrameworkElement parent = GLView;
                    while (parent != null && parent != element)
                    {
                        var potentialParent = parent.Parent;
                        if (potentialParent != null)
                            parent = potentialParent as FrameworkElement;
                        else
                            parent = null;
                    }

                    if (parent == element)
                    {
                        FocusHelperControl.Focus(FocusState.Programmatic);
                        glViewHasFocus = true;
                    }
                }
            }
            mRenderer.EnqueueTask(() =>
                {
                    isGLViewFocused = glViewHasFocus;
                });
        }

        void PopulateMenuBar(string resourcePath)
        {
            MenuBarItem CreateMenuBarItem(string name)
            {
                var item = new MenuBarItem
                {
                    Title = name,
                };
                return item;
            }

            var isFullScreen = ApplicationView.GetForCurrentView().IsFullScreenMode;
            MenuBar.Visibility = (isFullScreen || isXbox) ? Visibility.Collapsed : Visibility.Visible;

            var fileItem = CreateMenuBarItem(LocalizationHelper.Localize("File"));

            AppendItem(fileItem, LocalizationHelper.Localize("Run Script…"), (sender, arg) =>
            {
                PickScript();
            });

            var scriptsItem = new MenuFlyoutSubItem
            {
                Text = LocalizationHelper.Localize("Scripts")
            };
            var scripts = CelestiaAppCore.ReadScripts(resourcePath + "\\scripts", true);
            if (scripts != null)
            {
                foreach (var script in scripts)
                {
                    AppendSubItem(scriptsItem, script.Title, (sender, arg) =>
                    {
                        mAppCore.RunScript(script.Filename);
                    });
                }
            }
            if (mExtraScriptFolder != null)
            {
                var extraScripts = CelestiaAppCore.ReadScripts(mExtraScriptFolder, true);
                if (extraScripts != null)
                {
                    foreach (var script in extraScripts)
                    {
                        AppendSubItem(scriptsItem, script.Title, (sender, arg) =>
                        {
                            mAppCore.RunScript(script.Filename);
                        });
                    }
                }
            }
            fileItem.Items.Add(scriptsItem);

            fileItem.Items.Add(new MenuFlyoutSeparator());

            AppendItem(fileItem, LocalizationHelper.Localize("Capture Image"), (sender, arg) =>
            {
                CaptureImage();
            }, new KeyboardAccelerator() { Key = VirtualKey.F10 });

            fileItem.Items.Add(new MenuFlyoutSeparator());

            AppendItem(fileItem, LocalizationHelper.Localize("Copy URL"), (sender, arg) =>
            {
                DataPackage dataPackage = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy
                };
                dataPackage.SetText(mAppCore.CurrentURL);
                Clipboard.SetContent(dataPackage);
            }, new KeyboardAccelerator() { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.C });
            AppendItem(fileItem, LocalizationHelper.Localize("Paste URL"), async (sender, arg) =>
            {
                DataPackageView dataPackageView = Clipboard.GetContent();
                if (dataPackageView.Contains(StandardDataFormats.Text))
                {
                    string text = await dataPackageView.GetTextAsync();
                    mRenderer.EnqueueTask(() =>
                    {
                        mAppCore.GoToURL(text);
                    });
                }
            }, new KeyboardAccelerator() { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.V });

            fileItem.Items.Add(new MenuFlyoutSeparator());

            AppendItem(fileItem, LocalizationHelper.Localize("Settings"), (sender, arg) =>
            {
                ShowSettings();
            });
            fileItem.Items.Add(new MenuFlyoutSeparator());

            if (!isXbox)
            {
                AppendItem(fileItem, LocalizationHelper.Localize("Open Custom Folder"), async (sender, arg) =>
                {
                    await Launcher.LaunchFolderAsync(Windows.Storage.ApplicationData.Current.LocalFolder);
                });

                fileItem.Items.Add(new MenuFlyoutSeparator());
            }

            AppendItem(fileItem, LocalizationHelper.Localize("Exit"), (sender, arg) =>
            {
                Application.Current.Exit();
            });

            void AppendCharEnterItem(MenuBarItem item, string title, short input, KeyboardAccelerator keyboardAccelerator = null)
            {
                AppendItem(item, title, (sender, arg) =>
                {
                    mRenderer.EnqueueTask(() =>
                    {
                        mAppCore.CharEnter(input);
                    });
                }, keyboardAccelerator);
            }

            var navigationItem = CreateMenuBarItem(LocalizationHelper.Localize("Navigation"));

            AppendCharEnterItem(navigationItem, LocalizationHelper.Localize("Select Sol"), 104, new KeyboardAccelerator() { Key = VirtualKey.H });
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

            var actions = new (string, short)[] {
                    ("Center Selection", 99),
                    ("Go to Selection", 103),
                    ("Follow Selection", 102),
                    ("Sync Orbit Selection", 121),
                    ("Track Selection", 116)
                };
            foreach (var action in actions)
            {
                AppendItem(navigationItem, LocalizationHelper.Localize(action.Item1), (sender, arg) =>
                {
                    mRenderer.EnqueueTask(() =>
                    {
                        mAppCore.CharEnter(action.Item2);
                    });
                }, new KeyboardAccelerator() { Key = (VirtualKey)(action.Item2 - 32) });
            }
            navigationItem.Items.Add(new MenuFlyoutSeparator());

            AppendItem(navigationItem, LocalizationHelper.Localize("Star Browser"), (sender, arg) =>
            {
                ShowBrowser();
            });
            AppendItem(navigationItem, LocalizationHelper.Localize("Eclipse Finder"), (sender, arg) =>
            {
                ShowEclipseFinder();
            });

            var timeItem = CreateMenuBarItem(LocalizationHelper.Localize("Time"));
            AppendCharEnterItem(timeItem, LocalizationHelper.Localize("10x Faster"), 108, new KeyboardAccelerator() { Key = VirtualKey.L });
            AppendCharEnterItem(timeItem, LocalizationHelper.Localize("10x Slower"), 107, new KeyboardAccelerator() { Key = VirtualKey.K });
            AppendCharEnterItem(timeItem, LocalizationHelper.Localize("Freeze"), 32, new KeyboardAccelerator() { Key = VirtualKey.Space });
            AppendCharEnterItem(timeItem, LocalizationHelper.Localize("Real Time"), 33);
            AppendCharEnterItem(timeItem, LocalizationHelper.Localize("Reverse Time"), 106, new KeyboardAccelerator() { Key = VirtualKey.J });

            timeItem.Items.Add(new MenuFlyoutSeparator());

            AppendItem(timeItem, LocalizationHelper.Localize("Set Time…"), (sender, arg) =>
            {
                ShowTimeSetting();
            });

            var viewItem = CreateMenuBarItem(LocalizationHelper.Localize("Views"));
            AppendCharEnterItem(viewItem, LocalizationHelper.Localize("Split Horizontally"), 18, new KeyboardAccelerator() { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.R });
            AppendCharEnterItem(viewItem, LocalizationHelper.Localize("Split Vertically"), 21, new KeyboardAccelerator() { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.U });
            AppendCharEnterItem(viewItem, LocalizationHelper.Localize("Delete Active View"), 127, new KeyboardAccelerator() { Key = VirtualKey.Delete });
            AppendCharEnterItem(viewItem, LocalizationHelper.Localize("Delete Other Views"), 4, new KeyboardAccelerator() { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.D });

            var bookmarkItem = CreateMenuBarItem(LocalizationHelper.Localize("Bookmarks"));
            AppendItem(bookmarkItem, LocalizationHelper.Localize("Add Bookmark"), (sender, arg) =>
            {
                ShowNewBookmark();
            });
            AppendItem(bookmarkItem, LocalizationHelper.Localize("Organize Bookmarks…"), (sender, arg) =>
            {
                ShowBookmarkOrganizer();
            });

            var helpItem = CreateMenuBarItem(LocalizationHelper.Localize("Help"));
            AppendCharEnterItem(helpItem, LocalizationHelper.Localize("Run Demo"), 100);
            helpItem.Items.Add(new MenuFlyoutSeparator());
            AppendItem(helpItem, LocalizationHelper.Localize("OpenGL Info"), (sender, arg) =>
            {
                ShowOpenGLInfo();
            });
            helpItem.Items.Add(new MenuFlyoutSeparator());
            AppendItem(helpItem, LocalizationHelper.Localize("Get Add-ons"), (sender, arg) =>
            {
                var queryItems = System.Web.HttpUtility.ParseQueryString("");
                queryItems.Add("lang", LocalizationHelper.Locale);
                var builder = new UriBuilder("https://celestia.mobi/resources/categories");
                builder.Query = queryItems.ToString();
                _ = Launcher.LaunchUriAsync(builder.Uri);
            });
            if (!isXbox)
            {
                AppendItem(helpItem, LocalizationHelper.Localize("Installed Add-ons"), (sender, arg) =>
                {
                    ShowAddonManagement();
                });
            }
            helpItem.Items.Add(new MenuFlyoutSeparator());
            AppendItem(helpItem, LocalizationHelper.Localize("User Guide"), (sender, arg) =>
            {
                _ = Launcher.LaunchUriAsync(new Uri("https://github.com/levinli303/Celestia/wiki"));
            });
            AppendItem(helpItem, LocalizationHelper.Localize("About Celestia"), (sender, arg) =>
            {
                ShowAboutDialog();
            });

            MenuBar.Items.Add(fileItem);
            MenuBar.Items.Add(navigationItem);
            MenuBar.Items.Add(timeItem);
            MenuBar.Items.Add(viewItem);
            MenuBar.Items.Add(bookmarkItem);
            MenuBar.Items.Add(helpItem);
        }

        void AppendItem(MenuFlyout parent, string text, RoutedEventHandler click, KeyboardAccelerator accelerator = null)
        {
            var item = new MenuFlyoutItem
            {
                Text = text
            };
            if (accelerator != null)
                item.KeyboardAccelerators.Add(accelerator);
            if (click != null)
                item.Click += click;
            parent.Items.Add(item);
        }

        void AppendItem(MenuBarItem parent, string text, RoutedEventHandler click, KeyboardAccelerator accelerator = null)
        {
            var item = new MenuFlyoutItem
            {
                Text = text
            };
            if (accelerator != null)
                item.KeyboardAccelerators.Add(accelerator);
            if (click != null)
                item.Click += click;
            parent.Items.Add(item);
        }

        void AppendSubItem(MenuFlyoutSubItem parent, string text, RoutedEventHandler click)
        {
            var item = new MenuFlyoutItem
            {
                Text = text
            };
            if (click != null)
                item.Click += click;
            parent.Items.Add(item);
        }

        void AppendToggleSubItem(MenuFlyoutSubItem parent, string text, bool isChecked,  RoutedEventHandler click)
        {
            var item = new ToggleMenuFlyoutItem
            {
                IsChecked = isChecked,
                Text = text
            };
            item.Click += click;
            parent.Items.Add(item);
        }

        async void PickScript()
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads
            };
            picker.FileTypeFilter.Add(".cel");
            picker.FileTypeFilter.Add(".celx");
            var file = await picker.PickSingleFileAsync();
            if (file != null)
                OpenFileIfReady(file);
        }
        void ShowTourGuide()
        {
            ShowPage(typeof(TourGuidePage), new Size(400, 0), (mAppCore, mRenderer));
        }

        void ShowInfo(CelestiaSelection selection)
        {
            ShowPage(typeof(InfoPage), new Size(400, 0), (mAppCore, selection));
        }

        async void ShowSelectObject()
        {
            if (HasContentDialogOpen()) return;

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
                    mRenderer.EnqueueTask(() =>
                    {
                        mAppCore.Simulation.Selection = selection;
                    });
                }
            }
        }
        async void ShowGotoObject()
        {
            if (HasContentDialogOpen()) return;

            var dialog = new GotoObjectDialog(mAppCore);
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
                    mRenderer.EnqueueTask(() =>
                    {
                        mAppCore.Simulation.GoToLocation(location);
                    });
                }
            }
        }

        bool HasContentDialogOpen()
        {
            var openedpopups = VisualTreeHelper.GetOpenPopups(Window.Current);
            foreach (var popup in openedpopups)
            {
                if (popup.Child is ContentDialog)
                {
                    return true;
                }
            }
            return false;
        }

        void ShowObjectNotFound()
        {
            ContentDialogHelper.ShowAlert(this, LocalizationHelper.Localize("Object not found."));
        }

        void ShowBrowser()
        {
            ShowPage(typeof(BrowserPage), new Size(500, 0), (mAppCore, mRenderer));
        }

        void ShowEclipseFinder()
        {
            ShowPage(typeof(EclipseFinderPage), new Size(400, 0), (mAppCore, mRenderer));
        }

        async void ShowTimeSetting()
        {
            if (HasContentDialogOpen()) return;

            var time = DateTimeOffset.Now;
            try
            {
                time = mAppCore.Simulation.Time;
            }
            catch {} // Catch all exceptions

            var dialog = new TimeSettingDialog(time);
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var date = dialog.DisplayDate;
                mRenderer.EnqueueTask(() =>
                {
                    mAppCore.Simulation.Time = date;
                });
            }
        }

        void ShowSettings()
        {
            ShowPage(typeof(Settings.SettingsPage), new Size(500, 0), (mAppCore, AppSettings, AvailableLanguages));
        }

        void ShowPage(Type pageType, Size size, object parameter)
        {
            OverlayBackground.Visibility = Visibility.Visible;
            OverlayBackground.Width = size.Width;
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
            // Disable focus on menubar
            foreach (var item in MenuBar.Items)
            {
                item.IsTabStop = false;
            }
            if (ClosePanelButton.IsLoaded)
                ClosePanelButton.Focus(FocusState.Programmatic);
            else
                ClosePanelButton.Loaded += (s, _) =>
                {
                    ((Button)s).Focus(FocusState.Programmatic);
                };
            mRenderer.EnqueueTask(() =>
            {
                hasOverlayOpen = true;
            });
        }

        void ShowBookmarkOrganizer()
        {
            ShowPage(typeof(BookmarkOrganizerPage), new Size(400, 0), (mAppCore, mRenderer));
        }

        void ShowNewBookmark()
        {
            ShowPage(typeof(NewBookmarkPage), new Size(400, 0), (mAppCore, mRenderer));
        }
        void ShowOpenGLInfo()
        {
            mRenderer.EnqueueTask(() =>
            {
                string info = mAppCore.RenderInfo;
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    ShowOpenGLInfo(info);
                });
            });
        }

        async void ShowOpenGLInfo(string Info)
        {
            if (HasContentDialogOpen()) return;

            var dialog = new InfoDialog(Info)
            {
                Title = LocalizationHelper.Localize("OpenGL Info")
            };
            await dialog.ShowAsync();
        }

        void ShowAddonManagement()
        {
            ShowPage(typeof(Addon.ResourceManagerPage), new Size(450, 0), new Addon.ResourceManagerPageParameter(mAppCore, mRenderer));
        }

        async void ShowAboutDialog()
        {
            if (HasContentDialogOpen()) return;

            var dialog = new AboutDialog(defaultResourcePath + "\\AUTHORS", defaultResourcePath + "\\TRANSLATORS");
            await dialog.ShowAsync();
        }

        async Task<string> GetLocale(string LocalePath)
        {
            if (AvailableLanguages == null)
            {
                var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(LocalePath);
                var files = await folder.GetFoldersAsync();
                var availableLocales = new List<string>();
                foreach (var file in files)
                {
                    availableLocales.Add(file.Name);
                }

                availableLocales.Sort();
                AvailableLanguages = availableLocales.ToArray();
            }
            var resourceLoader = ResourceLoader.GetForViewIndependentUse();
            return resourceLoader.GetString("CelestiaLanguage");
        }

        private async Task<JsonObject> ReadDefaultSettings()
        {
            var installedFolder = Windows.ApplicationModel.Package.Current.InstalledLocation;
            try
            {
                var defaultsFile = await installedFolder.GetFileAsync("defaults.json");
                var content = await FileIO.ReadTextAsync(defaultsFile);
                return JsonObject.Parse(content);
            }
            catch { return null; }
        }

        private void ApplySettings(JsonObject defaultSettings)
        {
            if (defaultSettings == null) return;
            var customSettings = ApplicationData.Current.LocalSettings;
            foreach (var kvp in defaultSettings)
            {
                object customSetting;
                var hasCustomSetting = customSettings.Values.TryGetValue(kvp.Key, out customSetting);
                var booleanEntry = CelestiaExtension.GetBooleanEntryByName(kvp.Key);
                if (booleanEntry != CelestiaSettingBooleanEntry.None)
                {
                    bool currentValue = false;
                    if (kvp.Value.ValueType == JsonValueType.Boolean)
                        currentValue = kvp.Value.GetBoolean();
                    else if (kvp.Value.ValueType == JsonValueType.Number)
                        currentValue = kvp.Value.GetNumber() > 0.5;
                    if (hasCustomSetting && customSetting is bool boolValue)
                        currentValue = boolValue;
                    CelestiaExtension.SetCelestiaBooleanValue(mAppCore, booleanEntry, currentValue);
                    continue;
                }
                var int32Entry = CelestiaExtension.GetInt32EntryByName(kvp.Key);
                if (int32Entry != CelestiaSettingInt32Entry.None)
                {
                    int currentValue = (int)kvp.Value.GetNumber();
                    if (hasCustomSetting && customSetting is int intValue)
                        currentValue = intValue;
                    CelestiaExtension.SetCelestiaInt32Value(mAppCore, int32Entry, currentValue);
                    continue;
                }
                var singleEntry = CelestiaExtension.GetSingleEntryByName(kvp.Key);
                if (singleEntry != CelestiaSettingSingleEntry.None)
                {
                    float currentValue = (float)kvp.Value.GetNumber();
                    if (hasCustomSetting && customSetting is float floatValue)
                        currentValue = floatValue;
                    CelestiaExtension.SetCelestiaSingleValue(mAppCore, singleEntry, currentValue);
                    continue;
                }
            }
        }

        private MenuFlyoutItemBase CreateMenuItem(CelestiaBrowserItem item)
        {
            var menu = new MenuFlyoutSubItem
            {
                Text = item.Name
            };

            menu.Loaded += (sender, args) =>
            {
                var senderMenu = (MenuFlyoutSubItem)sender;
                if (senderMenu.Items.Count > 0) return;

                var children = new List<MenuFlyoutItemBase>();
                var obj = item.Object;
                if (obj != null)
                {
                    var selectItem = new MenuFlyoutItem
                    {
                        Text = LocalizationHelper.Localize("Select")
                    };
                    selectItem.Click += (newSender, newArgs) =>
                    {
                        mRenderer.EnqueueTask(() =>
                        {
                            var selection = new CelestiaSelection(obj);
                            mAppCore.Simulation.Selection = selection;
                        });
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
                foreach (var child in children)
                {
                    senderMenu.Items.Add(child);
                }
            };
            return menu;
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

                var savePicker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedStartLocation =
                    Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
                };
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

        private void ClosePanelButton_Click(object sender, RoutedEventArgs e)
        {
            ClosePanel();
        }
        private void ClosePanel() {
            OverlayBackground.Visibility = Visibility.Collapsed;
            OverlayContainer.Content = null;
            // enable focus on menubar
            foreach (var item in MenuBar.Items)
            {
                item.IsTabStop = true;
            }
            // move focus to either menubar or GLView
            if (MenuBar.Visibility == Visibility.Visible)
            {
                MenuBar.Items[0].Focus(FocusState.Programmatic);
            }
            else
            {
                FocusHelperControl.Focus(FocusState.Programmatic);
            }
            mRenderer.EnqueueTask(() =>
            {
                hasOverlayOpen = false;
            });
        }
    }
}
