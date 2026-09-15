using UnityEngine;

namespace CityMCP
{
    public class CityMCPPanel : MonoBehaviour
    {
        public static CityMCPPanel? Instance { get; private set; }

        private bool m_Visible;
        private Rect m_WindowRect = new Rect(20, 80, 340, 220);
        private string m_LinkCode = "---";
        private int m_Population;
        private int m_Money;
        private int m_Happiness;
        private string m_Status = "Connecting...";

        public string LinkCode
        {
            get => m_LinkCode;
            set => m_LinkCode = value ?? "---";
        }

        public string StatusText
        {
            get => m_Status;
            set => m_Status = value ?? "";
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            Instance = null;
        }

        public void Toggle() => m_Visible = !m_Visible;
        public void Show() => m_Visible = true;
        public void Hide() => m_Visible = false;

        public void UpdateStats(int population, int money, int happiness)
        {
            m_Population = population;
            m_Money = money;
            m_Happiness = happiness;
        }

        private void OnGUI()
        {
            if (!m_Visible) return;

            m_WindowRect = GUI.Window(7331001, m_WindowRect, DrawWindow, "CityMCP");
        }

        private void DrawWindow(int id)
        {
            var oldColor = GUI.color;

            GUI.color = new Color(0.3f, 1f, 0.5f);
            GUI.Label(new Rect(15, 30, 200, 30), "Link Code:");
            var oldFont = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = 24;
            GUI.Label(new Rect(110, 25, 200, 40), m_LinkCode);
            GUI.skin.label.fontSize = oldFont;

            GUI.color = new Color(0.5f, 0.7f, 1f);
            GUI.Label(new Rect(15, 65, 300, 20), "city.wilbot.link");

            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            GUI.Box(new Rect(15, 90, 310, 1), "");

            GUI.color = new Color(0.8f, 0.8f, 0.3f);
            GUI.Label(new Rect(15, 100, 300, 20), m_Status);

            GUI.color = Color.white;
            GUI.Label(new Rect(15, 125, 300, 20), $"Population: {m_Population:N0}");
            GUI.Label(new Rect(15, 148, 300, 20), $"Treasury: ${m_Money:N0}");
            GUI.Label(new Rect(15, 171, 300, 20), $"Happiness: {m_Happiness}%");

            GUI.color = oldColor;
            GUI.DragWindow();
        }
    }
}
