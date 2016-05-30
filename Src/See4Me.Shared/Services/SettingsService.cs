﻿using Plugin.Settings;
using Plugin.Settings.Abstractions;
using System;

namespace See4Me.Services
{
    public class SettingsService : ISettingsService
    {
        private const string CAMERA_PANEL = "CameraPanel";

        private ISettings settings;

        public SettingsService()
        {
            settings = CrossSettings.Current;
        }

        public CameraPanel CameraPanel
        {
            get
            {
                var setting = settings.GetValueOrDefault(CAMERA_PANEL, CameraPanel.Back.ToString());
                return (CameraPanel)Enum.Parse(typeof(CameraPanel), setting);
            }
            set { settings.AddOrUpdateValue(CAMERA_PANEL, value.ToString()); }
        }
    }
}
