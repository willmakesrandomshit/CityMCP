using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace CityMCP
{
    public class Mod : IMod
    {
        public const string ModName = "CityMCP";
        public static ILog Log = LogManager.GetLogger("CityMCP").SetShowsErrorsInUI(false);
        public static Setting? Settings { get; private set; }
        public static RelayClient? Relay { get; private set; }

        private BridgeHttpServer? m_Server;

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info("CityMCP loading...");

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(ModName, Settings, new Setting(this));

            updateSystem.UpdateAt<BridgeSystem>(SystemUpdatePhase.MainLoop);

            m_Server = new BridgeHttpServer(2828);
            m_Server.Start();

            Relay = new RelayClient();
            Relay.Start();

            Log.Info("CityMCP Beta loaded successfully. Local API: http://127.0.0.1:2828/");
        }

        public void OnDispose()
        {
            Log.Info("CityMCP disposing...");
            Relay?.Stop();
            Relay = null;
            m_Server?.Stop();
            m_Server = null;
            Settings?.UnregisterInOptionsUI();
            Settings = null;
        }
    }
}
