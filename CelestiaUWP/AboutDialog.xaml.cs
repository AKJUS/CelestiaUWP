﻿//
// AboutDialog.xaml.cs
//
// Copyright © 2021 Celestia Development Team. All rights reserved.
//
// This program is free software, you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 2
// of the License, or (at your option) any later version.
//

using CelestiaUWP.Helper;
using System;
using System.IO;
using System.Text;
using Windows.ApplicationModel;
using Windows.UI.Xaml.Controls;

namespace CelestiaUWP
{
    public sealed partial class AboutDialog : ContentDialog
    {
        public AboutDialog(string authorFilePath, string translatorFilePath)
        {
            this.InitializeComponent();
            Title = LocalizationHelper.Localize("About Celestia");
            PrimaryButtonText = LocalizationHelper.Localize("OK");
            Package package = Package.Current;
            PackageId packageId = package.Id;
            PackageVersion version = packageId.Version;

            VersionLabel.Text = string.Format("Celestia {0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);

            LoadAuthorTranslator(authorFilePath, translatorFilePath);
        }

        private async void LoadAuthorTranslator(string authorFilePath, string translatorFilePath)
        {
            try
            {
                var authorFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(authorFilePath);
                using (var inputStream = await authorFile.OpenStreamForReadAsync())
                using (var outputStream = new MemoryStream())
                {
                    inputStream.CopyTo(outputStream);
                    AuthorLabel.Text = Encoding.UTF8.GetString(outputStream.GetBuffer());
                    AuthorTitleLabel.Text = LocalizationHelper.Localize("Authors:");
                }
                var tanslatorFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(translatorFilePath);
                using (var inputStream = await tanslatorFile.OpenStreamForReadAsync())
                using (var outputStream = new MemoryStream())
                {
                    inputStream.CopyTo(outputStream);
                    TranslatorLabel.Text = Encoding.UTF8.GetString(outputStream.GetBuffer());
                    TranslatorTitleLabel.Text = LocalizationHelper.Localize("Translators:");
                }
            }
            catch { };

        }
    }
}
