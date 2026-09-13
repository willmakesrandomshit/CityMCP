using System;
using System.Collections.Concurrent;
using Colossal.Logging;
using Game;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Rendering;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CityMCP
{
    public class BridgeSystem : GameSystemBase
    {
        public static BridgeSystem? Instance { get; private set; }

        private SimulationSystem? m_SimulationSystem;
        private CitySystem? m_CitySystem;
        private CameraUpdateSystem? m_CameraUpdateSystem;
        private ResidentialDemandSystem? m_ResidentialDemandSystem;
        private CommercialDemandSystem? m_CommercialDemandSystem;
        private IndustrialDemandSystem? m_IndustrialDemandSystem;
        private TrafficFlowSystem? m_TrafficFlowSystem;
        private PlanetarySystem? m_PlanetarySystem;
        private ElectricityStatisticsSystem? m_ElectricityStats;
        private WaterStatisticsSystem? m_WaterStats;

        private EntityQuery m_RuinsQuery;

        private readonly ConcurrentQueue<Action> m_ActionQueue = new ConcurrentQueue<Action>();

        public void EnqueueAction(Action action)
        {
            m_ActionQueue.Enqueue(action);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_CameraUpdateSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_ResidentialDemandSystem = World.GetOrCreateSystemManaged<ResidentialDemandSystem>();
            m_CommercialDemandSystem = World.GetOrCreateSystemManaged<CommercialDemandSystem>();
            m_IndustrialDemandSystem = World.GetOrCreateSystemManaged<IndustrialDemandSystem>();
            m_TrafficFlowSystem = World.GetOrCreateSystemManaged<TrafficFlowSystem>();
            m_PlanetarySystem = World.GetOrCreateSystemManaged<PlanetarySystem>();
            m_ElectricityStats = World.GetOrCreateSystemManaged<ElectricityStatisticsSystem>();
            m_WaterStats = World.GetOrCreateSystemManaged<WaterStatisticsSystem>();

            m_RuinsQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<Abandoned>(),
                    ComponentType.ReadOnly<Destroyed>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });

            Mod.Log.Info("BridgeSystem (Full CityMCP Suite) initialized.");
        }

        protected override void OnDestroy()
        {
            Instance = null;
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            while (m_ActionQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Mod.Log.Error($"Error executing bridge action: {ex}");
                }
            }
        }

        public string GetStatusJson()
        {
            int population = 0;
            int happiness = 0;
            int health = 0;
            int money = 0;
            int xp = 0;
            int devPoints = 0;
            float speed = 1f;
            uint frame = 0;

            if (m_SimulationSystem != null)
            {
                speed = m_SimulationSystem.selectedSpeed;
                frame = m_SimulationSystem.frameIndex;
            }

            if (m_CitySystem != null && EntityManager.Exists(m_CitySystem.City))
            {
                money = m_CitySystem.moneyAmount;
                xp = m_CitySystem.XP;

                if (EntityManager.HasComponent<Population>(m_CitySystem.City))
                {
                    var popData = EntityManager.GetComponentData<Population>(m_CitySystem.City);
                    population = popData.m_Population;
                    happiness = popData.m_AverageHappiness;
                    health = popData.m_AverageHealth;
                }

                if (EntityManager.HasComponent<DevTreePoints>(m_CitySystem.City))
                {
                    devPoints = EntityManager.GetComponentData<DevTreePoints>(m_CitySystem.City).m_Points;
                }
            }

            float camX = 0, camY = 0, camZ = 0, zoom = 0;
            if (m_CameraUpdateSystem != null)
            {
                camX = m_CameraUpdateSystem.position.x;
                camY = m_CameraUpdateSystem.position.y;
                camZ = m_CameraUpdateSystem.position.z;
                zoom = m_CameraUpdateSystem.zoom;
            }

            int demResL = 0, demResM = 0, demResH = 0;
            if (m_ResidentialDemandSystem != null)
            {
                demResL = m_ResidentialDemandSystem.buildingDemand.x;
                demResM = m_ResidentialDemandSystem.buildingDemand.y;
                demResH = m_ResidentialDemandSystem.buildingDemand.z;
            }

            int demCom = 0;
            if (m_CommercialDemandSystem != null)
            {
                demCom = m_CommercialDemandSystem.buildingDemand;
            }

            int demInd = 0, demOff = 0;
            if (m_IndustrialDemandSystem != null)
            {
                demInd = m_IndustrialDemandSystem.industrialBuildingDemand;
                demOff = m_IndustrialDemandSystem.officeBuildingDemand;
            }

            int trafficFlow = m_TrafficFlowSystem?.cityAverageTrafficFlow ?? 100;

            return "{\n" +
                   $"  \"online\": true,\n" +
                   $"  \"population\": {population},\n" +
                   $"  \"happiness\": {happiness},\n" +
                   $"  \"health\": {health},\n" +
                   $"  \"money\": {money},\n" +
                   $"  \"xp\": {xp},\n" +
                   $"  \"devPoints\": {devPoints},\n" +
                   $"  \"trafficFlow\": {trafficFlow},\n" +
                   $"  \"simulationSpeed\": {speed},\n" +
                   $"  \"frameIndex\": {frame},\n" +
                   $"  \"camera\": {{ \"x\": {camX:F2}, \"y\": {camY:F2}, \"z\": {camZ:F2}, \"zoom\": {zoom:F2} }},\n" +
                   $"  \"demand\": {{\n" +
                   $"    \"residentialLow\": {demResL},\n" +
                   $"    \"residentialMedium\": {demResM},\n" +
                   $"    \"residentialHigh\": {demResH},\n" +
                   $"    \"commercial\": {demCom},\n" +
                   $"    \"industrial\": {demInd},\n" +
                   $"    \"office\": {demOff}\n" +
                   $"  }}\n" +
                   "}";
        }

        public string GetServicesJson()
        {
            int powerProd = m_ElectricityStats?.production ?? 0;
            int powerCons = m_ElectricityStats?.consumption ?? 0;
            int batteryCharge = m_ElectricityStats?.batteryCharge ?? 0;
            int batteryCap = m_ElectricityStats?.batteryCapacity ?? 0;

            int waterCap = m_WaterStats?.freshCapacity ?? 0;
            int waterCons = m_WaterStats?.freshConsumption ?? 0;
            int sewageCap = m_WaterStats?.sewageCapacity ?? 0;
            int sewageCons = m_WaterStats?.sewageConsumption ?? 0;

            return "{\n" +
                   $"  \"electricity\": {{\n" +
                   $"    \"productionMW\": {powerProd},\n" +
                   $"    \"consumptionMW\": {powerCons},\n" +
                   $"    \"batteryCharge\": {batteryCharge},\n" +
                   $"    \"batteryCapacity\": {batteryCap}\n" +
                   $"  }},\n" +
                   $"  \"water\": {{\n" +
                   $"    \"freshCapacity\": {waterCap},\n" +
                   $"    \"freshConsumption\": {waterCons},\n" +
                   $"    \"sewageCapacity\": {sewageCap},\n" +
                   $"    \"sewageConsumption\": {sewageCons}\n" +
                   $"  }}\n" +
                   "}";
        }

        public string GetTrafficJson()
        {
            int flow = m_TrafficFlowSystem?.cityAverageTrafficFlow ?? 100;
            int volume = m_TrafficFlowSystem?.cityAverageTrafficVolume ?? 0;

            return $"{{\n  \"cityAverageTrafficFlow\": {flow},\n  \"cityAverageTrafficVolume\": {volume}\n}}";
        }

        public int CleanRuins()
        {
            if (m_RuinsQuery.IsEmptyIgnoreFilter) return 0;

            var entities = m_RuinsQuery.ToEntityArray(Allocator.TempJob);
            int count = entities.Length;
            for (int i = 0; i < entities.Length; i++)
            {
                EntityManager.AddComponent<Deleted>(entities[i]);
            }
            entities.Dispose();

            Mod.Log.Info($"CleanRuins bulldozed {count} abandoned/destroyed entities.");
            return count;
        }

        public void AddMoney(int amount)
        {
            if (m_CitySystem != null && EntityManager.Exists(m_CitySystem.City))
            {
                if (EntityManager.HasComponent<PlayerMoney>(m_CitySystem.City))
                {
                    var pm = EntityManager.GetComponentData<PlayerMoney>(m_CitySystem.City);
                    int newTotal = pm.money + amount;
                    EntityManager.SetComponentData(m_CitySystem.City, new PlayerMoney(newTotal));
                    Mod.Log.Info($"Added {amount} funds. Treasury is now {newTotal}");
                }
            }
        }

        public void AddDevPoints(int points)
        {
            if (m_CitySystem != null && EntityManager.Exists(m_CitySystem.City))
            {
                if (EntityManager.HasComponent<DevTreePoints>(m_CitySystem.City))
                {
                    var dtp = EntityManager.GetComponentData<DevTreePoints>(m_CitySystem.City);
                    dtp.m_Points += points;
                    EntityManager.SetComponentData(m_CitySystem.City, dtp);
                    Mod.Log.Info($"Added {points} Dev Points. Total is now {dtp.m_Points}");
                }
            }
        }

        public void SetTimeOfDay(float hourOfDay)
        {
            if (m_PlanetarySystem != null)
            {
                m_PlanetarySystem.overrideTime = true;
                m_PlanetarySystem.normalizedTime = Mathf.Clamp01(hourOfDay / 24f);
                Mod.Log.Info($"Time of day locked to {hourOfDay:F1}h");
            }
        }

        public void SetSpeed(float speed)
        {
            if (m_SimulationSystem != null)
            {
                m_SimulationSystem.selectedSpeed = speed;
                Mod.Log.Info($"Simulation speed changed to {speed}");
            }
        }

        public void MoveCamera(float x, float y, float z, float zoom)
        {
            if (m_CameraUpdateSystem != null && m_CameraUpdateSystem.activeCameraController != null)
            {
                m_CameraUpdateSystem.activeCameraController.pivot = new float3(x, y, z);
                if (zoom > 0)
                {
                    m_CameraUpdateSystem.activeCameraController.zoom = zoom;
                }
            }
        }

        public void CaptureScreenshot(string filename)
        {
            ScreenCapture.CaptureScreenshot(filename);
            Mod.Log.Info($"Screenshot saved to {filename}");
        }
    }
}
