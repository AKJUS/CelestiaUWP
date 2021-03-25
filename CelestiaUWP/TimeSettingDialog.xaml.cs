﻿using CelestiaComponent;
using CelestiaUWP.Helper;
using System;
using System.ComponentModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace CelestiaUWP
{
    public sealed partial class TimeSettingDialog : ContentDialog, INotifyPropertyChanged
    {
        public DateTimeOffset DisplayDate
        {
            get {
                return Date.Date.AddMinutes(Time.TotalMinutes);
            }
            set
            {
                Date = value.Date;
                var time = value.TimeOfDay;
                Time = new TimeSpan(time.Hours, time.Minutes, 0);
            }
        }

        private DateTimeOffset mDate;
        private TimeSpan mTime;

        private DateTimeOffset Date
        {
            get { return mDate; }
            set
            {
                mDate = value;
                OnPropertyChanged("Date");
            }
        }
        private TimeSpan Time
        {
            get { return mTime; }
            set
            {
                mTime = value;
                OnPropertyChanged("Time");
            }
        }

        public TimeSettingDialog(DateTimeOffset original)
        {
            this.InitializeComponent();
            Title = LocalizationHelper.Localize("Set Simulation Time");
            PrimaryButtonText = LocalizationHelper.Localize("OK");
            SecondaryButtonText = LocalizationHelper.Localize("Cancel");
            CurrentTimeButton.Content = LocalizationHelper.Localize("Set to Current Time");
            DisplayDate = original;
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private void CurrentTimeButton_Click(object sender, RoutedEventArgs e)
        {
            DisplayDate = DateTime.Now;
        }
    }
}
