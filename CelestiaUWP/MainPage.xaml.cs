﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

using GLUWP;
using CelestiaComponent;
using Windows.UI.Core;

namespace CelestiaUWP
{
    public sealed partial class MainPage : Page
    {
        private CelestiaAppCore mAppCore;

        private static int leftMouseButton = 1;
        private static int middleMouseButton = 2;
        private static int rightMouseButton = 4;

        private Point? mLastLeftMousePosition = null;
        private Point? mLastRightMousePosition = null;

        public MainPage()
        {
            mAppCore = new CelestiaAppCore();

            InitializeComponent();

            TextBlock loadingText = new TextBlock();
            loadingText.Foreground = new SolidColorBrush(Colors.White);
            loadingText.HorizontalAlignment = HorizontalAlignment.Center;
            loadingText.VerticalAlignment = VerticalAlignment.Center;
            loadingText.FontSize = 30;

            grid.Children.Add(loadingText);

            GLView view = new GLView();
            view.Prepare += (sender) =>
            {
                CelestiaAppCore.InitGL();
                string installedPath = Windows.ApplicationModel.Package.Current.InstalledPath;
                Directory.SetCurrentDirectory(installedPath + "\\CelestiaResources");
                string[] extraPaths = { };
                if (!mAppCore.StartSimulation("celestia.cfg", extraPaths, delegate (string progress)
                {
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        loadingText.Text = progress;
                    });
                }))
                {
                    return false;
                }
                if (!mAppCore.StartRenderer())
                    return false;

                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    loadingText.Visibility = Visibility.Collapsed;
                });

                mAppCore.Start();
                return true;
            };
            view.Resize += (sender, width, height) =>
            {
                mAppCore.Resize(width, height);
            };
            view.Draw += (sender) =>
            {
                mAppCore.Tick();
                mAppCore.Draw();
            };
            view.PointerPressed += (sender, args) =>
            {
                if (args.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse)
                {
                    var properties = args.GetCurrentPoint(this).Properties;
                    var position = args.GetCurrentPoint(this).Position;
                    if (properties.IsLeftButtonPressed)
                    {
                        mAppCore.MouseButtonDown((float)position.X, (float)position.Y, leftMouseButton);
                        mLastLeftMousePosition = position;
                    }
                    if (properties.IsRightButtonPressed)
                    {
                        mAppCore.MouseButtonDown((float)position.X, (float)position.Y, rightMouseButton);
                        mLastRightMousePosition = position;
                    }
                }
            };
            view.PointerMoved += (sender, args) =>
            {
                if (args.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse)
                {
                    var properties = args.GetCurrentPoint(this).Properties;
                    var position = args.GetCurrentPoint(this).Position;

                    if (properties.IsLeftButtonPressed && mLastLeftMousePosition != null)
                    {
                        var lastPos = mLastLeftMousePosition;
                        var oldPos = (Point)lastPos;

                        var x = position.X - oldPos.X;
                        var y = position.Y - oldPos.Y;
                        mAppCore.MouseMove((float)x, (float)y, leftMouseButton);
                        mLastLeftMousePosition = position;
                    }
                    if (properties.IsRightButtonPressed && mLastRightMousePosition != null)
                    {
                        var lastPos = mLastRightMousePosition;
                        var oldPos = (Point)lastPos;

                        var x = position.X - oldPos.X;
                        var y = position.Y - oldPos.Y;
                        mAppCore.MouseMove((float)x, (float)y, rightMouseButton);
                        mLastRightMousePosition = position;
                    }
                }
            };
            view.PointerReleased += (sender, args) =>
            {
                if (args.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse)
                {
                    var properties = args.GetCurrentPoint(this).Properties;
                    var position = args.GetCurrentPoint(this).Position;
                    if (mLastLeftMousePosition != null && !properties.IsLeftButtonPressed)
                    {
                        mAppCore.MouseButtonUp((float)position.X, (float)position.Y, leftMouseButton);
                        mLastLeftMousePosition = null;
                    }
                    if (mLastRightMousePosition != null && !properties.IsRightButtonPressed)
                    {
                        mAppCore.MouseButtonUp((float)position.X, (float)position.Y, rightMouseButton);
                        mLastRightMousePosition = null;
                    }
                }
            };

            grid.Children.Add(view);

            MenuBar menuBar = new MenuBar();
            menuBar.HorizontalAlignment = HorizontalAlignment.Left;
            menuBar.VerticalAlignment = VerticalAlignment.Top;
            menuBar.Foreground = new SolidColorBrush(Colors.White);
            menuBar.Items.Add(new MenuBarItem { Title = "File" });
            grid.Children.Add(menuBar);
        }
    }
}
