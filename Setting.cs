using System;
using System.Collections.Generic;
using System.Diagnostics;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI.Widgets;
using UnityEngine;

namespace CityMCP
{
    [FileLocation(Mod.ModName)]
    [SettingsUIShowGroupName(RelayGroup)]
    public sealed class Setting : ModSetting
    {
        public const string MainSection = "Main";
        public const string RelayGroup = "CloudRelay";

        private string m_PairingCode = "Connecting...";

        public Setting(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(MainSection, RelayGroup)]
        [SettingsUIDisplayName(overrideValue: "Current link code")]
        [SettingsUIDescription(overrideValue: "Enter this six-digit code at city.wilbot.link. A new code is issued whenever the relay reconnects.")]
        public string PairingCode => m_PairingCode;

        [SettingsUIButton]
        [SettingsUISection(MainSection, RelayGroup)]
        [SettingsUIDisplayName(overrideValue: "Copy link code")]
        [SettingsUIDescription(overrideValue: "Copies the current six-digit link code to the clipboard.")]
        public bool CopyPairingCode
        {
            set
            {
                if (value && m_PairingCode.Length == 7 && m_PairingCode[3] == '-')
                    GUIUtility.systemCopyBuffer = m_PairingCode;
            }
        }

        [SettingsUIButton]
        [SettingsUISection(MainSection, RelayGroup)]
        [SettingsUIDisplayName(overrideValue: "Open CityMCP website")]
        [SettingsUIDescription(overrideValue: "Opens the companion dashboard in your default browser.")]
        public bool OpenCompanionWebsite
        {
            set
            {
                if (!value) return;
                try
                {
                    Process.Start(new ProcessStartInfo("https://city.wilbot.link/") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"Could not open CityMCP website: {ex.Message}");
                }
            }
        }

        [SettingsUIButton]
        [SettingsUISection(MainSection, RelayGroup)]
        [SettingsUIDisplayName(overrideValue: "Generate a new link code")]
        [SettingsUIDescription(overrideValue: "Reconnects to the secure relay and replaces the current link code.")]
        public bool RegeneratePairingCode
        {
            set
            {
                if (!value) return;
                UpdatePairingCode("Reconnecting...");
                Mod.Relay?.Reconnect();
            }
        }

        public void UpdatePairingCode(string value)
        {
            m_PairingCode = string.IsNullOrWhiteSpace(value) ? "Connecting..." : value;
        }

        public override void SetDefaults()
        {
        }
    }

    public sealed class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "CityMCP" },
                { m_Setting.GetOptionTabLocaleID(Setting.MainSection), "Connection" },
                { m_Setting.GetOptionGroupLocaleID(Setting.RelayGroup), "Cloud relay pairing" }
            };
        }

        public void Unload()
        {
        }
    }
}
