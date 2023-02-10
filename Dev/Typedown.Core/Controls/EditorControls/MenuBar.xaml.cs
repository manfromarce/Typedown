﻿using System;
using System.Reactive.Disposables;
using Typedown.Core.Utilities;
using Typedown.Core.ViewModels;
using Typedown.XamlUI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Typedown.Core.Controls
{
    public sealed partial class MenuBar : UserControl
    {
        public AppViewModel ViewModel => DataContext as AppViewModel;
        public SettingsViewModel Settings => ViewModel?.SettingsViewModel;

        private readonly CompositeDisposable disposables = new();

        public MenuBar()
        {
            InitializeComponent();
        }

        private void OnSizeChanged(object sender, Windows.UI.Xaml.SizeChangedEventArgs e)
        {
            if (TitleGrid != null)
            {
                if (ActualWidth / 2 > MenuBarControl.ActualWidth + TitleTextBlock.ActualWidth / 2 + 16)
                {
                    TitleGrid.Margin = new(0);
                    Grid.SetColumn(TitleGrid, 0);
                    Grid.SetColumnSpan(TitleGrid, 3);
                }
                else
                {
                    TitleGrid.Margin = new(0, 0, 46 * 3, 0);
                    Grid.SetColumn(TitleGrid, 1);
                    Grid.SetColumnSpan(TitleGrid, 2);
                }
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Settings.AppCompactMode)
            {
                var uiViewModel = ViewModel.UIViewModel;
                var oldCaptionHeight = uiViewModel.CaptionHeight;
                uiViewModel.CaptionHeight = 40;
                disposables.Add(Disposable.Create(() => uiViewModel.CaptionHeight = oldCaptionHeight));
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            disposables.Dispose();
        }
    }
}
