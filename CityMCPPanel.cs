using UnityEngine;

namespace CityMCP
{
    public class CityMCPPanel : MonoBehaviour
    {
        public static CityMCPPanel? Instance { get; private set; }

        private bool m_Visible;
        private bool m_ShowGuide;
        private string m_CopyFeedback = "";
        private Rect m_WindowRect = new Rect(20, 88, 360, 238);
        private string m_LinkCode = "---";
        private int m_Population;
        private int m_Money;
        private int m_Happiness;
        private bool m_HasCityStats;
        private string m_Status = "Connecting...";
        private GUIStyle? m_LinkCodeStyle;
        private GUIStyle? m_SectionLabelStyle;
        private GUIStyle? m_MetricLabelStyle;
        private GUIStyle? m_MetricValueStyle;

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
            m_ShowGuide = PlayerPrefs.GetInt("CityMCP.UI.Guide.v1", 1) != 0;
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
            m_HasCityStats = true;
        }

        public void ClearStats()
        {
            m_HasCityStats = false;
        }

        private void OnGUI()
        {
            if (!m_Visible) return;
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                Hide();
                Event.current.Use();
                return;
            }
            m_WindowRect.height = m_ShowGuide ? 336 : 238;
            m_WindowRect = GUI.Window(7331001, m_WindowRect, DrawWindow, "CityMCP");
        }

        private void DrawWindow(int id)
        {
            EnsureStyles();

            GUI.Label(new Rect(16, 30, 316, 18), "PAIRING CODE", m_SectionLabelStyle);
            GUI.Label(new Rect(16, 48, 316, 38), m_LinkCode, m_LinkCodeStyle);
            GUI.Label(new Rect(16, 86, 316, 18), "city.wilbot.link", m_SectionLabelStyle);

            GUI.Box(new Rect(16, 109, 328, 1), GUIContent.none);
            GUI.Label(new Rect(16, 116, 328, 20), m_Status, m_SectionLabelStyle);

            if (m_HasCityStats)
            {
                DrawMetric(16, 146, 102, "POPULATION", m_Population.ToString("N0"));
                DrawMetric(128, 146, 122, "TREASURY", $"${m_Money:N0}");
                DrawMetric(260, 146, 84, "HAPPINESS", $"{m_Happiness}%");
            }
            else
            {
                GUI.Label(new Rect(16, 151, 328, 24), "City metrics appear after a city loads.", m_SectionLabelStyle);
            }

            if (GUI.Button(new Rect(16, 187, 145, 24), m_ShowGuide ? "Hide guide" : "Quick guide"))
            {
                m_ShowGuide = !m_ShowGuide;
                PlayerPrefs.SetInt("CityMCP.UI.Guide.v1", m_ShowGuide ? 1 : 0);
                PlayerPrefs.Save();
            }
            if (GUI.Button(new Rect(173, 187, 171, 24), "Copy status snapshot"))
            {
                // Never include the pairing code or external-session secrets.
                GUIUtility.systemCopyBuffer = $"CityMCP {typeof(CityMCPPanel).Assembly.GetName().Version}\nGame: {Application.version}\nCity stats received: {m_HasCityStats}\nUI status snapshot; no logs included.";
                m_CopyFeedback = "Copied; pairing code excluded.";
            }
            GUI.Label(new Rect(16, 214, 328, 20), m_CopyFeedback, m_SectionLabelStyle);
            if (m_ShowGuide)
            {
                GUI.Label(new Rect(16, 240, 328, 82), "1. Open city.wilbot.link and enter the pairing code.\n2. Check the connection status before sending commands.\n3. Use a disposable city for mutation testing.\nPress Esc to close; drag the title to move this panel.", new GUIStyle(GUI.skin.label) { wordWrap = true });
            }
            GUI.DragWindow(new Rect(0, 0, 360, 24));
        }

        private void EnsureStyles()
        {
            if (m_LinkCodeStyle != null) return;

            m_LinkCodeStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                normal = { textColor = Color.white }
            };
            m_SectionLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                normal = { textColor = new Color(0.72f, 0.78f, 0.82f) }
            };
            m_MetricLabelStyle = new GUIStyle(m_SectionLabelStyle)
            {
                fontSize = 9,
                normal = { textColor = new Color(0.62f, 0.69f, 0.73f) }
            };
            m_MetricValueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = Color.white }
            };
        }

        private void DrawMetric(float x, float y, float width, string label, string value)
        {
            GUI.Label(new Rect(x, y, width, 16), label, m_MetricLabelStyle);
            GUI.Label(new Rect(x, y + 18, width, 23), value, m_MetricValueStyle);
        }
    }
}
