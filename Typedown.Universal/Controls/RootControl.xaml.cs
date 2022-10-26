﻿using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Typedown.Universal.Pages;
using Typedown.Universal.Utilities;
using Typedown.Universal.ViewModels;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;

namespace Typedown.Universal.Controls
{
    public sealed partial class RootControl : UserControl
    {
        public AppViewModel AppViewModel => DataContext as AppViewModel;

        private readonly ObservableCollection<Type> history = new();

        private readonly CompositeDisposable disposables = new();

        public RootControl()
        {
            InitializeComponent();
        }

        private void OnLoaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            disposables.Add(AppViewModel.NavigateCommand.OnExecute.Select(GetPageType).Subscribe(history.Add));
            disposables.Add(AppViewModel.GoBackCommand.OnExecute.Select(_ => history.Count - 1).Subscribe(history.RemoveAt));
            var historyObservable = history.GetCollectionObservable();
            disposables.Add(historyObservable.Subscribe(_ => AppViewModel.GoBackCommand.IsExecutable = history.Count > 1));
            disposables.Add(historyObservable.Select(x => x.EventArgs.Action).Select(GetTransition).Subscribe(t => Frame.Navigate(history.Last(), null, t)));
            history.Add(typeof(MainPage));
        }

        private void OnUnloaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            disposables.Clear();
            history.Clear();
        }

        public Type GetPageType(string pageName) => pageName switch
        {
            "Settings" => typeof(SettingsPage),
            _ => typeof(MainPage)
        };

        public SlideNavigationTransitionInfo GetTransition(NotifyCollectionChangedAction action) => new SlideNavigationTransitionInfo()
        {
            Effect = action switch
            {
                NotifyCollectionChangedAction.Add => SlideNavigationTransitionEffect.FromRight,
                _ => SlideNavigationTransitionEffect.FromLeft
            }
        };
    }
}
