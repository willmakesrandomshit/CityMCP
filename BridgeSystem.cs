using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.City;
using Game.Companies;
using Game.Common;
using Game.Citizens;
using Game.Net;
using Game.Objects;
using Game.Policies;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Game.Zones;
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
        private PrefabSystem? m_PrefabSystem;
        private ClimateSystem? m_ClimateSystem;
        private CityServiceBudgetSystem? m_BudgetSystem;
        private TransportLineSystem? m_TransportLineSystem;
        private GroundPollutionSystem? m_GroundPollution;
        private AirPollutionSystem? m_AirPollution;
        private NoisePollutionSystem? m_NoisePollution;
        private NaturalResourceSystem? m_NaturalResources;

        private EntityQuery m_RuinsQuery;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_EdgeQuery;
        private EntityQuery m_BlockQuery;
        private EntityQuery m_DistrictQuery;

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
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ClimateSystem = World.GetOrCreateSystemManaged<ClimateSystem>();
            m_BudgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            m_TransportLineSystem = World.GetOrCreateSystemManaged<TransportLineSystem>();
            m_GroundPollution = World.GetOrCreateSystemManaged<GroundPollutionSystem>();
            m_AirPollution = World.GetOrCreateSystemManaged<AirPollutionSystem>();
            m_NoisePollution = World.GetOrCreateSystemManaged<NoisePollutionSystem>();
            m_NaturalResources = World.GetOrCreateSystemManaged<NaturalResourceSystem>();

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

            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });

            m_EdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Edge>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });

            m_BlockQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Block>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });

            m_DistrictQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.District>()
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

        // ── EXISTING COMMANDS ──────────────────────────────────────────

        public string GetStatusJson()
        {
            int population = 0, happiness = 0, health = 0, money = 0, xp = 0, devPoints = 0;
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
                    var pop = EntityManager.GetComponentData<Population>(m_CitySystem.City);
                    population = pop.m_Population;
                    happiness = pop.m_AverageHappiness;
                    health = pop.m_AverageHealth;
                }
                if (EntityManager.HasComponent<DevTreePoints>(m_CitySystem.City))
                    devPoints = EntityManager.GetComponentData<DevTreePoints>(m_CitySystem.City).m_Points;
            }

            float camX = 0, camY = 0, camZ = 0, zoom = 0;
            if (m_CameraUpdateSystem != null)
            {
                camX = m_CameraUpdateSystem.position.x;
                camY = m_CameraUpdateSystem.position.y;
                camZ = m_CameraUpdateSystem.position.z;
                zoom = m_CameraUpdateSystem.zoom;
            }

            int dRL = 0, dRM = 0, dRH = 0;
            if (m_ResidentialDemandSystem != null)
            {
                dRL = m_ResidentialDemandSystem.buildingDemand.x;
                dRM = m_ResidentialDemandSystem.buildingDemand.y;
                dRH = m_ResidentialDemandSystem.buildingDemand.z;
            }
            int dC = m_CommercialDemandSystem?.buildingDemand ?? 0;
            int dI = m_IndustrialDemandSystem?.industrialBuildingDemand ?? 0;
            int dO = m_IndustrialDemandSystem?.officeBuildingDemand ?? 0;
            int tFlow = m_TrafficFlowSystem?.cityAverageTrafficFlow ?? 100;

            return "{" +
                $"\"online\":true,\"population\":{population},\"happiness\":{happiness},\"health\":{health}," +
                $"\"money\":{money},\"xp\":{xp},\"devPoints\":{devPoints},\"trafficFlow\":{tFlow}," +
                $"\"simulationSpeed\":{speed},\"frameIndex\":{frame}," +
                $"\"camera\":{{\"x\":{camX:F2},\"y\":{camY:F2},\"z\":{camZ:F2},\"zoom\":{zoom:F2}}}," +
                $"\"demand\":{{\"residentialLow\":{dRL},\"residentialMedium\":{dRM},\"residentialHigh\":{dRH}," +
                $"\"commercial\":{dC},\"industrial\":{dI},\"office\":{dO}}}" +
                "}";
        }

        public string GetServicesJson()
        {
            int pP = m_ElectricityStats?.production ?? 0;
            int pC = m_ElectricityStats?.consumption ?? 0;
            int bC = m_ElectricityStats?.batteryCharge ?? 0;
            int bCap = m_ElectricityStats?.batteryCapacity ?? 0;
            int wC = m_WaterStats?.freshCapacity ?? 0;
            int wU = m_WaterStats?.freshConsumption ?? 0;
            int sC = m_WaterStats?.sewageCapacity ?? 0;
            int sU = m_WaterStats?.sewageConsumption ?? 0;

            return "{" +
                $"\"electricity\":{{\"productionMW\":{pP},\"consumptionMW\":{pC},\"batteryCharge\":{bC},\"batteryCapacity\":{bCap}}}," +
                $"\"water\":{{\"freshCapacity\":{wC},\"freshConsumption\":{wU},\"sewageCapacity\":{sC},\"sewageConsumption\":{sU}}}" +
                "}";
        }

        public string GetTrafficJson()
        {
            int flow = m_TrafficFlowSystem?.cityAverageTrafficFlow ?? 100;
            int volume = m_TrafficFlowSystem?.cityAverageTrafficVolume ?? 0;
            return $"{{\"cityAverageTrafficFlow\":{flow},\"cityAverageTrafficVolume\":{volume}}}";
        }

        public int CleanRuins()
        {
            if (m_RuinsQuery.IsEmptyIgnoreFilter) return 0;
            var entities = m_RuinsQuery.ToEntityArray(Allocator.TempJob);
            int count = entities.Length;
            for (int i = 0; i < entities.Length; i++)
                EntityManager.AddComponent<Deleted>(entities[i]);
            entities.Dispose();
            Mod.Log.Info($"CleanRuins bulldozed {count} abandoned/destroyed entities.");
            return count;
        }

        public void AddMoney(int amount)
        {
            if (m_CitySystem != null && EntityManager.Exists(m_CitySystem.City) &&
                EntityManager.HasComponent<PlayerMoney>(m_CitySystem.City))
            {
                var pm = EntityManager.GetComponentData<PlayerMoney>(m_CitySystem.City);
                int newTotal = pm.money + amount;
                EntityManager.SetComponentData(m_CitySystem.City, new PlayerMoney(newTotal));
                Mod.Log.Info($"Added {amount} funds. Treasury is now {newTotal}");
            }
        }

        public void AddDevPoints(int points)
        {
            if (m_CitySystem != null && EntityManager.Exists(m_CitySystem.City) &&
                EntityManager.HasComponent<DevTreePoints>(m_CitySystem.City))
            {
                var dtp = EntityManager.GetComponentData<DevTreePoints>(m_CitySystem.City);
                dtp.m_Points += points;
                EntityManager.SetComponentData(m_CitySystem.City, dtp);
                Mod.Log.Info($"Added {points} Dev Points. Total is now {dtp.m_Points}");
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
            if (m_CameraUpdateSystem?.activeCameraController != null)
            {
                m_CameraUpdateSystem.activeCameraController.pivot = new float3(x, y, z);
                if (zoom > 0) m_CameraUpdateSystem.activeCameraController.zoom = zoom;
            }
        }

        public void CaptureScreenshot(string filename)
        {
            ScreenCapture.CaptureScreenshot(filename);
            Mod.Log.Info($"Screenshot saved to {filename}");
        }

        // ── ZONE COMMANDS ──────────────────────────────────────────────

        public int PaintZone(string zoneType, float radius)
        {
            if (m_BlockQuery.IsEmptyIgnoreFilter) return 0;

            float3 camPos = GetCameraPosition();
            ZoneType zone = ParseZoneType(zoneType);
            if (zone.m_Index == 0 && zoneType != "none" && zoneType != "dezone")
            {
                Mod.Log.Warn($"Unknown zone type: {zoneType}");
                return 0;
            }

            var blocks = m_BlockQuery.ToEntityArray(Allocator.TempJob);
            int painted = 0;

            for (int b = 0; b < blocks.Length; b++)
            {
                Entity blockEntity = blocks[b];
                Block block = EntityManager.GetComponentData<Block>(blockEntity);
                float dist = math.distance(camPos.xz, block.m_Position.xz);
                if (dist > radius) continue;

                var cells = EntityManager.GetBuffer<Cell>(blockEntity, false);
                for (int i = 0; i < cells.Length; i++)
                {
                    Cell cell = cells[i];
                    if ((cell.m_State & CellFlags.Visible) != 0 &&
                        (cell.m_State & CellFlags.Occupied) == 0)
                    {
                        cell.m_Zone = zone;
                        cells[i] = cell;
                        painted++;
                    }
                }
            }
            blocks.Dispose();
            Mod.Log.Info($"PaintZone: painted {painted} cells with {zoneType} in radius {radius}");
            return painted;
        }

        public int Dezone(float radius)
        {
            return PaintZone("none", radius);
        }

        public string GetZones(float radius)
        {
            float3 camPos = GetCameraPosition();
            int resLow = 0, resMid = 0, resHigh = 0, com = 0, ind = 0, off = 0, empty = 0;

            var blocks = m_BlockQuery.ToEntityArray(Allocator.TempJob);
            for (int b = 0; b < blocks.Length; b++)
            {
                Entity blockEntity = blocks[b];
                Block block = EntityManager.GetComponentData<Block>(blockEntity);
                float dist = math.distance(camPos.xz, block.m_Position.xz);
                if (dist > radius) continue;

                var cells = EntityManager.GetBuffer<Cell>(blockEntity);
                for (int i = 0; i < cells.Length; i++)
                {
                    Cell cell = cells[i];
                    if ((cell.m_State & CellFlags.Visible) == 0) continue;
                    ushort idx = cell.m_Zone.m_Index;
                    if (idx == 0) empty++;
                    else if (idx <= 2) resLow++;
                    else if (idx <= 5) resMid++;
                    else if (idx <= 8) resHigh++;
                    else if (idx <= 12) com++;
                    else if (idx <= 16) ind++;
                    else if (idx <= 20) off++;
                    else empty++;
                }
            }
            blocks.Dispose();

            return $"{{\"residentialLow\":{resLow},\"residentialMedium\":{resMid},\"residentialHigh\":{resHigh}," +
                   $"\"commercial\":{com},\"industrial\":{ind},\"office\":{off},\"unzoned\":{empty}}}";
        }

        // ── BULLDOZE COMMANDS ──────────────────────────────────────────

        public int Bulldoze(float radius)
        {
            float3 camPos = GetCameraPosition();
            int demolished = 0;

            var buildings = m_BuildingQuery.ToEntityArray(Allocator.TempJob);
            for (int i = 0; i < buildings.Length; i++)
            {
                Entity e = buildings[i];
                if (EntityManager.HasComponent<Game.Buildings.Building>(e))
                {
                    var bld = EntityManager.GetComponentData<Game.Buildings.Building>(e);
                    if (EntityManager.Exists(bld.m_RoadEdge) &&
                        EntityManager.HasComponent<Game.Net.Edge>(bld.m_RoadEdge))
                    {
                        var edge = EntityManager.GetComponentData<Game.Net.Edge>(bld.m_RoadEdge);
                        if (EntityManager.Exists(edge.m_Start) &&
                            EntityManager.HasComponent<Game.Net.Node>(edge.m_Start))
                        {
                            var node = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start);
                            float dist = math.distance(camPos, node.m_Position);
                            if (dist <= radius)
                            {
                                EntityManager.AddComponent<Deleted>(e);
                                demolished++;
                            }
                        }
                    }
                }
            }
            buildings.Dispose();

            var edges = m_EdgeQuery.ToEntityArray(Allocator.TempJob);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity e = edges[i];
                if (EntityManager.HasComponent<Game.Net.Edge>(e))
                {
                    var edge = EntityManager.GetComponentData<Game.Net.Edge>(e);
                    if (EntityManager.Exists(edge.m_Start) &&
                        EntityManager.HasComponent<Game.Net.Node>(edge.m_Start))
                    {
                        var node = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start);
                        float dist = math.distance(camPos, node.m_Position);
                        if (dist <= radius)
                        {
                            EntityManager.AddComponent<Deleted>(e);
                            demolished++;
                        }
                    }
                }
            }
            edges.Dispose();

            Mod.Log.Info($"Bulldozed {demolished} entities in radius {radius}");
            return demolished;
        }

        // ── BUDGET / TAX COMMANDS ──────────────────────────────────────

        public string GetBudget()
        {
            if (m_CitySystem == null || !EntityManager.Exists(m_CitySystem.City))
                return "{\"error\":\"City not ready\"}";

            Entity city = m_CitySystem.City;
            var sb = new StringBuilder();
            sb.Append("{\"taxRates\":[");

            if (EntityManager.HasBuffer<TaxRates>(city))
            {
                var taxes = EntityManager.GetBuffer<TaxRates>(city);
                for (int i = 0; i < taxes.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append($"{{\"index\":{i},\"rate\":{taxes[i].m_TaxRate}}}");
                }
            }

            sb.Append("],\"serviceFees\":[");

            if (EntityManager.HasBuffer<ServiceFee>(city))
            {
                var fees = EntityManager.GetBuffer<ServiceFee>(city);
                for (int i = 0; i < fees.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append($"{{\"resource\":\"{fees[i].m_Resource}\",\"fee\":{fees[i].m_Fee:F2}}}");
                }
            }

            sb.Append("]}");
            return sb.ToString();
        }

        public string SetTax(int index, int rate)
        {
            if (m_CitySystem == null || !EntityManager.Exists(m_CitySystem.City))
                return "{\"error\":\"City not ready\"}";

            Entity city = m_CitySystem.City;
            if (!EntityManager.HasBuffer<TaxRates>(city))
                return "{\"error\":\"No tax buffer\"}";

            var taxes = EntityManager.GetBuffer<TaxRates>(city);
            if (index < 0 || index >= taxes.Length)
                return "{\"error\":\"Invalid tax index\"}";

            int old = taxes[index].m_TaxRate;
            taxes[index] = new TaxRates { m_TaxRate = math.clamp(rate, -100, 100) };
            Mod.Log.Info($"Tax {index}: {old} -> {rate}");
            return $"{{\"index\":{index},\"oldRate\":{old},\"newRate\":{rate}}}";
        }

        public string SetServiceFee(string resource, float fee)
        {
            if (m_CitySystem == null || !EntityManager.Exists(m_CitySystem.City))
                return "{\"error\":\"City not ready\"}";

            Entity city = m_CitySystem.City;
            if (!EntityManager.HasBuffer<ServiceFee>(city))
                return "{\"error\":\"No service fee buffer\"}";

            var fees = EntityManager.GetBuffer<ServiceFee>(city);
            PlayerResource res = ParsePlayerResource(resource);
            bool found = false;

            for (int i = 0; i < fees.Length; i++)
            {
                if (fees[i].m_Resource == res)
                {
                    float oldFee = fees[i].m_Fee;
                    fees[i] = new ServiceFee { m_Resource = res, m_Fee = math.clamp(fee, 0f, 2f) };
                    Mod.Log.Info($"ServiceFee {resource}: {oldFee:F2} -> {fee:F2}");
                    found = true;
                    break;
                }
            }

            return found ? $"{{\"resource\":\"{resource}\",\"newFee\":{fee:F2}}}" :
                           $"{{\"error\":\"Resource '{resource}' not found\"}}";
        }

        // ── POLICY COMMANDS ────────────────────────────────────────────

        public string GetPolicies()
        {
            if (m_CitySystem == null || !EntityManager.Exists(m_CitySystem.City))
                return "{\"error\":\"City not ready\"}";

            Entity city = m_CitySystem.City;
            if (!EntityManager.HasBuffer<Policy>(city))
                return "{\"policies\":[]}";

            var policies = EntityManager.GetBuffer<Policy>(city);
            var sb = new StringBuilder();
            sb.Append("{\"policies\":[");

            for (int i = 0; i < policies.Length; i++)
            {
                if (i > 0) sb.Append(",");
                Entity policyEntity = policies[i].m_Policy;
                string name = policyEntity.ToString();
                if (EntityManager.HasComponent<PrefabRef>(policyEntity))
                {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(policyEntity);
                    name = prefab.m_Prefab.ToString();
                }
                bool active = (policies[i].m_Flags & PolicyFlags.Active) != 0;
                sb.Append($"{{\"name\":\"{EscapeJson(name)}\",\"active\":{active.ToString().ToLower()}}}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        public string TogglePolicy(string policyName)
        {
            if (m_CitySystem == null || !EntityManager.Exists(m_CitySystem.City))
                return "{\"error\":\"City not ready\"}";

            Entity city = m_CitySystem.City;
            if (!EntityManager.HasBuffer<Policy>(city))
                return "{\"error\":\"No policy buffer\"}";

            var policies = EntityManager.GetBuffer<Policy>(city);
            for (int i = 0; i < policies.Length; i++)
            {
                Entity policyEntity = policies[i].m_Policy;
                string name = policyEntity.ToString();
                if (EntityManager.HasComponent<PrefabRef>(policyEntity))
                {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(policyEntity);
                    name = prefab.m_Prefab.ToString();
                }

                if (string.Equals(name, policyName, StringComparison.OrdinalIgnoreCase))
                {
                    bool wasActive = (policies[i].m_Flags & PolicyFlags.Active) != 0;
                    var newPolicy = policies[i];
                    newPolicy.m_Flags = wasActive ? (PolicyFlags)0 : PolicyFlags.Active;
                    policies[i] = newPolicy;
                    Mod.Log.Info($"Policy '{name}' toggled: {wasActive} -> {!wasActive}");
                    return $"{{\"name\":\"{EscapeJson(name)}\",\"active\":{(!wasActive).ToString().ToLower()}}}";
                }
            }

            return $"{{\"error\":\"Policy '{EscapeJson(policyName)}' not found\"}}";
        }

        // ── INFO COMMANDS ──────────────────────────────────────────────

        public string GetBuildings(float radius, int limit)
        {
            float3 camPos = GetCameraPosition();
            var sb = new StringBuilder();
            sb.Append("{\"buildings\":[");

            var buildings = m_BuildingQuery.ToEntityArray(Allocator.TempJob);
            int count = 0;
            for (int i = 0; i < buildings.Length && count < limit; i++)
            {
                Entity e = buildings[i];
                if (!EntityManager.HasComponent<Game.Buildings.Building>(e)) continue;
                var bld = EntityManager.GetComponentData<Game.Buildings.Building>(e);

                float3 pos = float3.zero;
                if (EntityManager.Exists(bld.m_RoadEdge) &&
                    EntityManager.HasComponent<Game.Net.Edge>(bld.m_RoadEdge))
                {
                    var edge = EntityManager.GetComponentData<Game.Net.Edge>(bld.m_RoadEdge);
                    if (EntityManager.Exists(edge.m_Start) &&
                        EntityManager.HasComponent<Game.Net.Node>(edge.m_Start))
                    {
                        pos = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start).m_Position;
                    }
                }

                float dist = math.distance(camPos, pos);
                if (dist > radius) continue;

                string name = "unknown";
                if (EntityManager.HasComponent<PrefabRef>(e))
                {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(e);
                    name = prefab.m_Prefab.ToString();
                }

                if (count > 0) sb.Append(",");
                sb.Append($"{{\"name\":\"{EscapeJson(name)}\",\"x\":{pos.x:F1},\"y\":{pos.y:F1},\"z\":{pos.z:F1}}}");
                count++;
            }
            buildings.Dispose();

            sb.Append("]}");
            return sb.ToString();
        }

        public string GetZoneBlocks(float radius, int limit)
        {
            float3 camPos = GetCameraPosition();
            var sb = new StringBuilder();
            sb.Append("{\"blocks\":[");

            var blocks = m_BlockQuery.ToEntityArray(Allocator.TempJob);
            int count = 0;
            for (int i = 0; i < blocks.Length && count < limit; i++)
            {
                Entity blockEntity = blocks[i];
                Block block = EntityManager.GetComponentData<Block>(blockEntity);
                float dist = math.distance(camPos.xz, block.m_Position.xz);
                if (dist > radius) continue;

                int zoned = 0, empty = 0;
                var cells = EntityManager.GetBuffer<Cell>(blockEntity);
                for (int c = 0; c < cells.Length; c++)
                {
                    if ((cells[c].m_State & CellFlags.Visible) == 0) continue;
                    if (cells[c].m_Zone.m_Index == 0) empty++;
                    else zoned++;
                }

                if (count > 0) sb.Append(",");
                sb.Append($"{{\"x\":{block.m_Position.x:F1},\"z\":{block.m_Position.z:F1}," +
                          $"\"sizeX\":{block.m_Size.x},\"sizeY\":{block.m_Size.y}," +
                          $"\"zonedCells\":{zoned},\"emptyCells\":{empty}}}");
                count++;
            }
            blocks.Dispose();

            sb.Append("]}");
            return sb.ToString();
        }

        public string GetRoads(float radius, int limit)
        {
            float3 camPos = GetCameraPosition();
            var sb = new StringBuilder();
            sb.Append("{\"roads\":[");

            var edges = m_EdgeQuery.ToEntityArray(Allocator.TempJob);
            int count = 0;
            for (int i = 0; i < edges.Length && count < limit; i++)
            {
                Entity e = edges[i];
                if (!EntityManager.HasComponent<Game.Net.Edge>(e)) continue;
                var edge = EntityManager.GetComponentData<Game.Net.Edge>(e);

                float3 pos = float3.zero;
                if (EntityManager.Exists(edge.m_Start) &&
                    EntityManager.HasComponent<Game.Net.Node>(edge.m_Start))
                {
                    pos = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start).m_Position;
                }

                float dist = math.distance(camPos, pos);
                if (dist > radius) continue;

                string name = "unknown";
                if (EntityManager.HasComponent<PrefabRef>(e))
                {
                    var prefab = EntityManager.GetComponentData<PrefabRef>(e);
                    name = prefab.m_Prefab.ToString();
                }

                if (count > 0) sb.Append(",");
                sb.Append($"{{\"name\":\"{EscapeJson(name)}\",\"x\":{pos.x:F1},\"y\":{pos.y:F1},\"z\":{pos.z:F1}}}");
                count++;
            }
            edges.Dispose();

            sb.Append("]}");
            return sb.ToString();
        }

        // ── BUILDING PLACEMENT ─────────────────────────────────────────

        public string PlaceBuilding(string prefabName, float x, float y, float z, float rotation)
        {
            if (m_PrefabSystem == null)
                return "{\"error\":\"PrefabSystem not ready\"}";

            Entity prefabEntity = FindPrefabByName(prefabName);
            if (prefabEntity == Entity.Null)
                return $"{{\"error\":\"Prefab '{EscapeJson(prefabName)}' not found\"}}";

            float3 position = new float3(x, y, z);
            quaternion rot = quaternion.Euler(0, math.radians(rotation), 0);

            Entity defEntity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(defEntity, new CreationDefinition
            {
                m_Prefab = prefabEntity,
                m_Flags = CreationFlags.Permanent
            });
            EntityManager.AddComponentData(defEntity, new ObjectDefinition
            {
                m_Position = position,
                m_Rotation = rot,
                m_Probability = 100
            });
            EntityManager.AddComponentData(defEntity, default(Updated));

            Mod.Log.Info($"Placed building '{prefabName}' at ({x:F1}, {y:F1}, {z:F1})");
            return $"{{\"placed\":\"{EscapeJson(prefabName)}\",\"x\":{x:F1},\"y\":{y:F1},\"z\":{z:F1}}}";
        }

        // ── ROAD BUILDING ─────────────────────────────────────────────

        public string BuildRoad(string prefabName, float x1, float y1, float z1, float x2, float y2, float z2)
        {
            if (m_PrefabSystem == null)
                return "{\"error\":\"PrefabSystem not ready\"}";

            Entity prefabEntity = FindPrefabByName(prefabName);
            if (prefabEntity == Entity.Null)
                return $"{{\"error\":\"Road prefab '{EscapeJson(prefabName)}' not found\"}}";

            float3 start = new float3(x1, y1, z1);
            float3 end = new float3(x2, y2, z2);

            float3 mid = (start + end) * 0.5f;
            Bezier4x3 curve = new Bezier4x3(start, mid, mid, end);

            float length = math.distance(start, end);

            Entity defEntity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(defEntity, new CreationDefinition
            {
                m_Prefab = prefabEntity,
                m_Flags = CreationFlags.Permanent | CreationFlags.SubElevation
            });

            NetCourse course = default;
            course.m_Curve = curve;
            course.m_Length = length;
            course.m_StartPosition = new CoursePos
            {
                m_Position = start,
                m_Rotation = quaternion.LookRotation(end - start, new float3(0, 1, 0)),
                m_Flags = CoursePosFlags.IsFirst
            };
            course.m_EndPosition = new CoursePos
            {
                m_Position = end,
                m_Rotation = quaternion.LookRotation(start - end, new float3(0, 1, 0)),
                m_Flags = CoursePosFlags.IsLast
            };
            course.m_Elevation = new float2(0, 0);
            course.m_FixedIndex = -1;

            EntityManager.AddComponentData(defEntity, course);
            EntityManager.AddComponentData(defEntity, default(Updated));

            Mod.Log.Info($"Built road '{prefabName}' from ({x1:F1},{y1:F1},{z1:F1}) to ({x2:F1},{y2:F1},{z2:F1})");
            return "{\"built\":\"" + EscapeJson(prefabName) +
                   "\",\"start\":{\"x\":" + x1.ToString("F1", CultureInfo.InvariantCulture) +
                   ",\"y\":" + y1.ToString("F1", CultureInfo.InvariantCulture) +
                   ",\"z\":" + z1.ToString("F1", CultureInfo.InvariantCulture) +
                   "},\"end\":{\"x\":" + x2.ToString("F1", CultureInfo.InvariantCulture) +
                   ",\"y\":" + y2.ToString("F1", CultureInfo.InvariantCulture) +
                   ",\"z\":" + z2.ToString("F1", CultureInfo.InvariantCulture) +
                   "},\"length\":" + length.ToString("F1", CultureInfo.InvariantCulture) + "}";
        }

        // ── PREFAB LISTING ────────────────────────────────────────────

        public string ListPrefabs(string type, int limit)
        {
            if (m_PrefabSystem == null)
                return "{\"error\":\"PrefabSystem not ready\"}";

            var sb = new StringBuilder();
            sb.Append("{\"prefabs\":[");

            var prefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabData>() }
            });

            var entities = prefabQuery.ToEntityArray(Allocator.TempJob);
            int count = 0;
            string lowerType = type.ToLowerInvariant();

            for (int i = 0; i < entities.Length && count < limit; i++)
            {
                Entity e = entities[i];
                string name = m_PrefabSystem.GetPrefabName(e);
                if (string.IsNullOrEmpty(name)) continue;

                bool match = lowerType switch
                {
                    "building" or "bld" => EntityManager.HasComponent<BuildingData>(e),
                    "road" or "net" => EntityManager.HasComponent<RoadData>(e) || EntityManager.HasComponent<NetData>(e),
                    "zone" or "zoning" => EntityManager.HasComponent<ZoneBlockData>(e),
                    "all" or "" or null => true,
                    _ => name.ToLowerInvariant().Contains(lowerType)
                };

                if (!match) continue;

                string typeName = "unknown";
                if (EntityManager.HasComponent<BuildingData>(e)) typeName = "building";
                else if (EntityManager.HasComponent<RoadData>(e)) typeName = "road";
                else if (EntityManager.HasComponent<NetData>(e)) typeName = "net";
                else if (EntityManager.HasComponent<ZoneBlockData>(e)) typeName = "zone";

                if (count > 0) sb.Append(",");
                sb.Append($"{{\"name\":\"{EscapeJson(name)}\",\"type\":\"{typeName}\"}}");
                count++;
            }
            entities.Dispose();

            sb.Append("]}");
            return sb.ToString();
        }

        // ── WEATHER COMMANDS ──────────────────────────────────────────

        public string GetWeather()
        {
            if (m_ClimateSystem == null) return "{\"error\":\"ClimateSystem not ready\"}";

            float precip = m_ClimateSystem.precipitation;
            float temp = m_ClimateSystem.temperature;
            float clouds = m_ClimateSystem.cloudiness;
            float fogVal = m_ClimateSystem.fog;
            float aurora = m_ClimateSystem.aurora;
            bool raining = m_ClimateSystem.isRaining;
            bool snowing = m_ClimateSystem.isSnowing;

            return "{" +
                $"\"precipitation\":{precip:F3},\"temperature\":{temp:F1}," +
                $"\"cloudiness\":{clouds:F3},\"fog\":{fogVal:F3},\"aurora\":{aurora:F3}," +
                $"\"isRaining\":{raining.ToString().ToLower()},\"isSnowing\":{snowing.ToString().ToLower()}" +
                "}";
        }

        public string SetWeather(string property, float value)
        {
            if (m_ClimateSystem == null) return "{\"error\":\"ClimateSystem not ready\"}";

            switch (property.ToLowerInvariant())
            {
                case "precipitation":
                case "rain":
                    m_ClimateSystem.precipitation.overrideValue = math.clamp(value, 0f, 1f);
                    break;
                case "temperature":
                case "temp":
                    m_ClimateSystem.temperature.overrideValue = math.clamp(value, -40f, 60f);
                    break;
                case "cloudiness":
                case "clouds":
                    m_ClimateSystem.cloudiness.overrideValue = math.clamp(value, 0f, 1f);
                    break;
                case "fog":
                    m_ClimateSystem.fog.overrideValue = math.clamp(value, 0f, 1f);
                    break;
                case "aurora":
                    m_ClimateSystem.aurora.overrideValue = math.clamp(value, 0f, 1f);
                    break;
                default:
                    return $"{{\"error\":\"Unknown property '{EscapeJson(property)}'. Use: precipitation, temperature, cloudiness, fog, aurora\"}}";
            }

            return $"{{\"property\":\"{EscapeJson(property)}\",\"value\":{value:F3}}}";
        }

        // ── SERVICE BUDGET COMMANDS ──────────────────────────────────

        public string GetServiceBudgets()
        {
            if (m_BudgetSystem == null) return "{\"error\":\"BudgetSystem not ready\"}";

            var serviceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CollectedCityServiceBudgetData>() }
            });

            var services = serviceQuery.ToEntityArray(Allocator.TempJob);
            var sb = new StringBuilder();
            sb.Append("{\"services\":[");

            for (int i = 0; i < services.Length; i++)
            {
                Entity e = services[i];
                string name = m_PrefabSystem?.GetPrefabName(e) ?? e.ToString();
                int budget = m_BudgetSystem.GetServiceBudget(e);
                var collected = EntityManager.GetComponentData<CollectedCityServiceBudgetData>(e);

                if (i > 0) sb.Append(",");
                sb.Append($"{{\"name\":\"{EscapeJson(name)}\",\"budget\":{budget}," +
                          $"\"buildings\":{collected.m_Count},\"workers\":{collected.m_Workplaces.x}}}");
            }
            services.Dispose();

            sb.Append("]}");
            return sb.ToString();
        }

        public string SetServiceBudget(string service, int budget)
        {
            if (m_BudgetSystem == null) return "{\"error\":\"BudgetSystem not ready\"}";

            var serviceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CollectedCityServiceBudgetData>() }
            });

            var services = serviceQuery.ToEntityArray(Allocator.TempJob);
            string lower = service.ToLowerInvariant();
            Entity found = Entity.Null;

            for (int i = 0; i < services.Length; i++)
            {
                string name = m_PrefabSystem?.GetPrefabName(services[i]) ?? "";
                if (name.ToLowerInvariant().Contains(lower))
                {
                    found = services[i];
                    break;
                }
            }
            services.Dispose();

            if (found == Entity.Null)
                return $"{{\"error\":\"Service '{EscapeJson(service)}' not found\"}}";

            int old = m_BudgetSystem.GetServiceBudget(found);
            m_BudgetSystem.SetServiceBudget(found, math.clamp(budget, 0, 200));
            string foundName = m_PrefabSystem?.GetPrefabName(found) ?? found.ToString();
            return $"{{\"service\":\"{EscapeJson(foundName)}\",\"oldBudget\":{old},\"newBudget\":{budget}}}";
        }

        // ── DISTRICT COMMANDS ────────────────────────────────────────

        public string GetDistricts()
        {
            if (m_DistrictQuery.IsEmptyIgnoreFilter) return "{\"districts\":[]}";

            var entities = m_DistrictQuery.ToEntityArray(Allocator.TempJob);
            var sb = new StringBuilder();
            sb.Append("{\"districts\":[");

            for (int i = 0; i < entities.Length; i++)
            {
                Entity e = entities[i];
                var district = EntityManager.GetComponentData<Game.Areas.District>(e);
                string name = m_PrefabSystem?.GetPrefabName(e) ?? e.ToString();
                uint options = district.m_OptionMask;

                if (i > 0) sb.Append(",");
                sb.Append($"{{\"name\":\"{EscapeJson(name)}\",\"index\":{i}," +
                          $"\"paidParking\":{((options & 1u) != 0).ToString().ToLower()}," +
                          $"\"forbidCombustion\":{((options & 2u) != 0).ToString().ToLower()}," +
                          $"\"forbidTransit\":{((options & 4u) != 0).ToString().ToLower()}," +
                          $"\"forbidHeavy\":{((options & 8u) != 0).ToString().ToLower()}," +
                          $"\"forbidBikes\":{((options & 16u) != 0).ToString().ToLower()}}}");
            }
            entities.Dispose();

            sb.Append("]}");
            return sb.ToString();
        }

        public string GetDistrictPolicies(int districtIndex)
        {
            if (m_DistrictQuery.IsEmptyIgnoreFilter) return "{\"error\":\"No districts\"}";

            var entities = m_DistrictQuery.ToEntityArray(Allocator.TempJob);
            if (districtIndex < 0 || districtIndex >= entities.Length)
            {
                entities.Dispose();
                return $"{{\"error\":\"District index {districtIndex} out of range (0-{entities.Length - 1})\"}}";
            }

            Entity e = entities[districtIndex];
            entities.Dispose();

            if (!EntityManager.HasBuffer<Policy>(e))
                return "{\"policies\":[]}";

            var policies = EntityManager.GetBuffer<Policy>(e);
            var sb = new StringBuilder();
            sb.Append("{\"policies\":[");

            for (int i = 0; i < policies.Length; i++)
            {
                if (i > 0) sb.Append(",");
                Entity policyEntity = policies[i].m_Policy;
                string name = policyEntity.ToString();
                if (m_PrefabSystem != null)
                    name = m_PrefabSystem.GetPrefabName(policyEntity);
                bool active = (policies[i].m_Flags & PolicyFlags.Active) != 0;
                sb.Append($"{{\"name\":\"{EscapeJson(name)}\",\"active\":{active.ToString().ToLower()},\"adjustment\":{policies[i].m_Adjustment:F2}}}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        // ── POLLUTION COMMANDS ───────────────────────────────────────

        public string GetPollution(float x, float z)
        {
            float3 pos = new float3(x, 0, z);
            float ground = 0, air = 0, noise = 0;

            if (m_GroundPollution != null)
            {
                var map = m_GroundPollution.GetMap(true, out _);
                if (map.IsCreated)
                {
                    var p = GroundPollutionSystem.GetPollution(pos, map);
                    ground = (float)p.m_Pollution / 32767f;
                }
            }
            if (m_AirPollution != null)
            {
                var map = m_AirPollution.GetMap(true, out _);
                if (map.IsCreated)
                {
                    var p = AirPollutionSystem.GetPollution(pos, map);
                    air = (float)p.m_Pollution / 32767f;
                }
            }
            if (m_NoisePollution != null)
            {
                var map = m_NoisePollution.GetMap(true, out _);
                if (map.IsCreated)
                {
                    var p = NoisePollutionSystem.GetPollution(pos, map);
                    noise = (float)p.m_Pollution / 32767f;
                }
            }

            return "{" +
                $"\"position\":{{\"x\":{x:F1},\"z\":{z:F1}}}," +
                $"\"ground\":{ground:F4},\"air\":{air:F4},\"noise\":{noise:F4}" +
                "}";
        }

        // ── NATURAL RESOURCES ────────────────────────────────────────

        public string GetResources(float x, float z)
        {
            float3 pos = new float3(x, 0, z);
            float fertility = 0, ore = 0, oil = 0, fish = 0;

            if (m_NaturalResources != null)
            {
                var map = m_NaturalResources.GetMap(true, out _);
                if (map.IsCreated)
                {
                    fertility = NaturalResourceSystem.GetFertilityAmount(pos, map).m_Base / 10000f;
                    ore = NaturalResourceSystem.GetOreAmount(pos, map).m_Base / 10000f;
                    oil = NaturalResourceSystem.GetOilAmount(pos, map).m_Base / 10000f;
                    fish = NaturalResourceSystem.GetFishAmount(pos, map).m_Base / 10000f;
                }
            }

            return "{" +
                $"\"position\":{{\"x\":{x:F1},\"z\":{z:F1}}}," +
                $"\"fertility\":{fertility:F4},\"ore\":{ore:F4},\"oil\":{oil:F4},\"fish\":{fish:F4}" +
                "}";
        }

        // ── TRANSPORT LINE COMMANDS ──────────────────────────────────

        public string GetTransportLines()
        {
            var lineQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.Route>(),
                    ComponentType.ReadWrite<TransportLine>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });

            if (lineQuery.IsEmptyIgnoreFilter) return "{\"lines\":[]}";

            var entities = lineQuery.ToEntityArray(Allocator.TempJob);
            var sb = new StringBuilder();
            sb.Append("{\"lines\":[");

            for (int i = 0; i < entities.Length; i++)
            {
                Entity e = entities[i];
                var line = EntityManager.GetComponentData<TransportLine>(e);
                string name = m_PrefabSystem?.GetPrefabName(e) ?? e.ToString();

                int stopCount = 0;
                if (EntityManager.HasBuffer<RouteWaypoint>(e))
                    stopCount = EntityManager.GetBuffer<RouteWaypoint>(e).Length;

                int vehicleCount = 0;
                if (EntityManager.HasBuffer<RouteVehicle>(e))
                    vehicleCount = EntityManager.GetBuffer<RouteVehicle>(e).Length;

                bool notEnough = (line.m_Flags & TransportLineFlags.NotEnoughVehicles) != 0;
                bool requireVehicles = (line.m_Flags & TransportLineFlags.RequireVehicles) != 0;

                if (i > 0) sb.Append(",");
                sb.Append($"{{\"name\":\"{EscapeJson(name)}\",\"stops\":{stopCount}," +
                          $"\"vehicles\":{vehicleCount},\"ticketPrice\":{line.m_TicketPrice}," +
                          $"\"vehicleInterval\":{line.m_VehicleInterval:F1}," +
                          $"\"notEnoughVehicles\":{notEnough.ToString().ToLower()}}}");
            }
            entities.Dispose();

            sb.Append("]}");
            return sb.ToString();
        }

        // ── BUILDING DETAIL COMMANDS ─────────────────────────────────

        public string GetBuildingInfo(float x, float y, float z)
        {
            float3 pos = new float3(x, y, z);
            Entity nearest = Entity.Null;
            float bestDist = float.MaxValue;

            var buildings = m_BuildingQuery.ToEntityArray(Allocator.TempJob);
            for (int i = 0; i < buildings.Length; i++)
            {
                Entity e = buildings[i];
                if (!EntityManager.HasComponent<Game.Buildings.Building>(e)) continue;
                var bld = EntityManager.GetComponentData<Game.Buildings.Building>(e);

                if (EntityManager.Exists(bld.m_RoadEdge) &&
                    EntityManager.HasComponent<Game.Net.Edge>(bld.m_RoadEdge))
                {
                    var edge = EntityManager.GetComponentData<Game.Net.Edge>(bld.m_RoadEdge);
                    if (EntityManager.Exists(edge.m_Start) &&
                        EntityManager.HasComponent<Game.Net.Node>(edge.m_Start))
                    {
                        var node = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start);
                        float dist = math.distance(pos, node.m_Position);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            nearest = e;
                        }
                    }
                }
            }
            buildings.Dispose();

            if (nearest == Entity.Null)
                return "{\"error\":\"No building found nearby\"}";

            var sb = new StringBuilder();
            sb.Append("{");

            string name = m_PrefabSystem?.GetPrefabName(nearest) ?? "unknown";
            sb.Append($"\"name\":\"{EscapeJson(name)}\"");

            if (EntityManager.HasComponent<Game.Buildings.Building>(nearest))
            {
                var bld = EntityManager.GetComponentData<Game.Buildings.Building>(nearest);
                sb.Append($",\"flags\":\"{bld.m_Flags}\"");
            }

            if (EntityManager.HasComponent<BuildingCondition>(nearest))
            {
                var cond = EntityManager.GetComponentData<BuildingCondition>(nearest);
                sb.Append($",\"condition\":{cond.m_Condition}");
            }

            if (EntityManager.HasComponent<ServiceUsage>(nearest))
            {
                var usage = EntityManager.GetComponentData<ServiceUsage>(nearest);
                sb.Append($",\"serviceUsage\":{usage.m_Usage:F2}");
            }

            if (EntityManager.HasBuffer<InstalledUpgrade>(nearest))
            {
                var upgrades = EntityManager.GetBuffer<InstalledUpgrade>(nearest);
                sb.Append($",\"upgrades\":{upgrades.Length}");
            }

            if (EntityManager.HasBuffer<Employee>(nearest))
            {
                var employees = EntityManager.GetBuffer<Employee>(nearest, true);
                sb.Append($",\"employees\":{employees.Length}");
            }

            if (EntityManager.HasComponent<Game.Buildings.School>(nearest))
            {
                var school = EntityManager.GetComponentData<Game.Buildings.School>(nearest);
                sb.Append($",\"type\":\"school\",\"studentWellbeing\":{school.m_StudentWellbeing}");
            }

            if (EntityManager.HasComponent<Game.Buildings.Hospital>(nearest))
            {
                var hospital = EntityManager.GetComponentData<Game.Buildings.Hospital>(nearest);
                sb.Append($",\"type\":\"hospital\",\"hasAmbulances\":{((hospital.m_Flags & Game.Buildings.HospitalFlags.HasAvailableAmbulances) != 0).ToString().ToLower()}");
            }

            sb.Append("}");
            return sb.ToString();
        }

        public string DemolishBuilding(float x, float y, float z)
        {
            float3 pos = new float3(x, y, z);
            Entity nearest = Entity.Null;
            float bestDist = float.MaxValue;

            var buildings = m_BuildingQuery.ToEntityArray(Allocator.TempJob);
            for (int i = 0; i < buildings.Length; i++)
            {
                Entity e = buildings[i];
                if (!EntityManager.HasComponent<Game.Buildings.Building>(e)) continue;
                var bld = EntityManager.GetComponentData<Game.Buildings.Building>(e);

                if (EntityManager.Exists(bld.m_RoadEdge) &&
                    EntityManager.HasComponent<Game.Net.Edge>(bld.m_RoadEdge))
                {
                    var edge = EntityManager.GetComponentData<Game.Net.Edge>(bld.m_RoadEdge);
                    if (EntityManager.Exists(edge.m_Start) &&
                        EntityManager.HasComponent<Game.Net.Node>(edge.m_Start))
                    {
                        var node = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start);
                        float dist = math.distance(pos, node.m_Position);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            nearest = e;
                        }
                    }
                }
            }
            buildings.Dispose();

            if (nearest == Entity.Null)
                return "{\"error\":\"No building found nearby\"}";

            string name = m_PrefabSystem?.GetPrefabName(nearest) ?? "unknown";
            EntityManager.AddComponent<Deleted>(nearest);
            return $"{{\"demolished\":\"{EscapeJson(name)}\"}}";
        }

        // ── TERRAIN COMMANDS ─────────────────────────────────────────

        public string SetTerrainHeight(float x, float z, float radius, float strength)
        {
            if (m_PrefabSystem == null) return "{\"error\":\"PrefabSystem not ready\"}";

            Entity brushPrefab = FindPrefabByName("Terrain");
            if (brushPrefab == Entity.Null)
                return "{\"error\":\"Terrain brush prefab not found. Use listprefabs to find terrain prefabs.\"}";

            float3 pos = new float3(x, 0, z);
            float3 end = pos + new float3(0.01f, 0, 0);

            Entity defEntity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(defEntity, new CreationDefinition
            {
                m_Prefab = brushPrefab,
                m_Flags = CreationFlags.Permanent
            });
            EntityManager.AddComponentData(defEntity, new BrushDefinition
            {
                m_Tool = brushPrefab,
                m_Line = new Colossal.Mathematics.Line3.Segment(pos, end),
                m_Size = radius,
                m_Strength = strength,
                m_Time = 1f,
                m_Target = pos,
                m_Start = pos
            });
            EntityManager.AddComponentData(defEntity, default(Updated));

            Mod.Log.Info($"SetTerrainHeight at ({x:F1},{z:F1}) radius={radius:F1} strength={strength:F1}");
            return $"{{\"position\":{{\"x\":{x:F1},\"z\":{z:F1}}},\"radius\":{radius:F1},\"strength\":{strength:F1}}}";
        }

        public string FlattenTerrain(float x, float z, float radius, float targetHeight)
        {
            if (m_PrefabSystem == null) return "{\"error\":\"PrefabSystem not ready\"}";

            Entity brushPrefab = FindPrefabByName("Terrain");
            if (brushPrefab == Entity.Null)
                return "{\"error\":\"Terrain brush prefab not found. Use listprefabs to find terrain prefabs.\"}";

            float3 pos = new float3(x, 0, z);
            float3 end = pos + new float3(0.01f, 0, 0);
            float3 target = new float3(x, targetHeight, z);

            Entity defEntity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(defEntity, new CreationDefinition
            {
                m_Prefab = brushPrefab,
                m_Flags = CreationFlags.Permanent
            });
            EntityManager.AddComponentData(defEntity, new BrushDefinition
            {
                m_Tool = brushPrefab,
                m_Line = new Colossal.Mathematics.Line3.Segment(pos, end),
                m_Size = radius,
                m_Strength = 1f,
                m_Time = 1f,
                m_Target = target,
                m_Start = target
            });
            EntityManager.AddComponentData(defEntity, default(Updated));

            Mod.Log.Info($"FlattenTerrain at ({x:F1},{z:F1}) radius={radius:F1} height={targetHeight:F1}");
            return $"{{\"position\":{{\"x\":{x:F1},\"z\":{z:F1}}},\"radius\":{radius:F1},\"targetHeight\":{targetHeight:F1}}}";
        }

        // ── VEHICLE SPAWN COMMANDS ───────────────────────────────────

        public string SpawnVehicle(string prefabName, float x, float y, float z)
        {
            if (m_PrefabSystem == null) return "{\"error\":\"PrefabSystem not ready\"}";

            Entity vehiclePrefab = FindPrefabByName(prefabName);
            if (vehiclePrefab == Entity.Null)
                return $"{{\"error\":\"Vehicle prefab '{EscapeJson(prefabName)}' not found\"}}";

            if (!EntityManager.HasComponent<ObjectData>(vehiclePrefab))
                return "{\"error\":\"Prefab is not a vehicle\"}";

            var objectData = EntityManager.GetComponentData<ObjectData>(vehiclePrefab);
            if (!objectData.m_Archetype.Valid)
                return "{\"error\":\"Vehicle has no valid archetype\"}";

            float3 position = new float3(x, y, z);
            quaternion rotation = quaternion.identity;

            Entity entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new Game.Objects.Transform
            {
                m_Position = position,
                m_Rotation = rotation
            });
            EntityManager.AddComponentData(entity, new PrefabRef { m_Prefab = vehiclePrefab });

            Mod.Log.Info($"Spawned vehicle '{prefabName}' at ({x:F1},{y:F1},{z:F1})");
            return $"{{\"spawned\":\"{EscapeJson(prefabName)}\",\"x\":{x:F1},\"y\":{y:F1},\"z\":{z:F1}}}";
        }

        public string SpawnParkedVehicle(string prefabName, float x, float y, float z)
        {
            if (m_PrefabSystem == null) return "{\"error\":\"PrefabSystem not ready\"}";

            Entity vehiclePrefab = FindPrefabByName(prefabName);
            if (vehiclePrefab == Entity.Null)
                return $"{{\"error\":\"Vehicle prefab '{EscapeJson(prefabName)}' not found\"}}";

            if (!EntityManager.HasComponent<MovingObjectData>(vehiclePrefab))
                return "{\"error\":\"Prefab is not a vehicle\"}";

            var movingData = EntityManager.GetComponentData<MovingObjectData>(vehiclePrefab);
            if (!movingData.m_StoppedArchetype.Valid)
                return "{\"error\":\"Vehicle has no valid stopped archetype\"}";

            float3 position = new float3(x, y, z);
            quaternion rotation = quaternion.Euler(0, 0, 0);

            Entity entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new Game.Objects.Transform
            {
                m_Position = position,
                m_Rotation = rotation
            });
            EntityManager.AddComponentData(entity, new PrefabRef { m_Prefab = vehiclePrefab });

            Mod.Log.Info($"Spawned parked vehicle '{prefabName}' at ({x:F1},{y:F1},{z:F1})");
            return $"{{\"spawned\":\"{EscapeJson(prefabName)}\",\"x\":{x:F1},\"y\":{y:F1},\"z\":{z:F1},\"parked\":true}}";
        }

        // ── CITIZEN / HOUSEHOLD SPAWN ────────────────────────────────

        public string SpawnCitizens(int count)
        {
            if (m_PrefabSystem == null) return "{\"error\":\"PrefabSystem not ready\"}";
            if (m_CitySystem == null || !EntityManager.Exists(m_CitySystem.City))
                return "{\"error\":\"City not ready\"}";

            Entity householdPrefab = FindPrefabByComponent<HouseholdData>();
            if (householdPrefab == Entity.Null)
                return "{\"error\":\"Household prefab not found\"}";

            Entity citizenPrefab = FindPrefabByComponent<CitizenData>();
            if (citizenPrefab == Entity.Null)
                return "{\"error\":\"Citizen prefab not found\"}";

            if (!EntityManager.HasComponent<ArchetypeData>(householdPrefab))
                return "{\"error\":\"Household has no archetype data\"}";

            var archetypeData = EntityManager.GetComponentData<ArchetypeData>(householdPrefab);
            int spawned = 0;

            for (int i = 0; i < count; i++)
            {
                Entity household = EntityManager.CreateEntity();
                EntityManager.AddComponentData(household, new PrefabRef { m_Prefab = householdPrefab });

                Entity citizen = EntityManager.CreateEntity();
                EntityManager.AddComponentData(citizen, new Game.Citizens.Citizen());
                EntityManager.AddComponentData(citizen, new HouseholdMember { m_Household = household });
                EntityManager.AddComponentData(citizen, new PrefabRef { m_Prefab = citizenPrefab });
                EntityManager.AddComponent<Created>(citizen);

                if (EntityManager.HasBuffer<HouseholdCitizen>(household))
                {
                    EntityManager.GetBuffer<HouseholdCitizen>(household)
                        .Add(new HouseholdCitizen { m_Citizen = citizen });
                }

                spawned++;
            }

            Mod.Log.Info($"Spawned {spawned} citizens in new households");
            return $"{{\"spawned\":{spawned}}}";
        }

        // ── ZONE DENSITY COMMANDS ────────────────────────────────────

        public string SetDensity(float x, float z, float radius, float density)
        {
            float3 camPos = new float3(x, 0, z);
            int set = 0;

            var edges = m_EdgeQuery.ToEntityArray(Allocator.TempJob);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity e = edges[i];
                if (!EntityManager.HasComponent<Game.Net.Edge>(e)) continue;
                var edge = EntityManager.GetComponentData<Game.Net.Edge>(e);

                if (EntityManager.Exists(edge.m_Start) &&
                    EntityManager.HasComponent<Game.Net.Node>(edge.m_Start))
                {
                    var node = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start);
                    float dist = math.distance(camPos, node.m_Position);
                    if (dist <= radius)
                    {
                        if (EntityManager.HasComponent<Density>(e))
                        {
                            EntityManager.SetComponentData(e, new Density { m_Density = density });
                            set++;
                        }
                    }
                }
            }
            edges.Dispose();

            Mod.Log.Info($"SetDensity on {set} road edges to {density:F2}");
            return $"{{\"edges\":{set},\"density\":{density:F2}}}";
        }

        // ── BUILDING UPGRADE COMMANDS ────────────────────────────────

        public string InstallUpgrade(float x, float y, float z, string upgradeName)
        {
            if (m_PrefabSystem == null) return "{\"error\":\"PrefabSystem not ready\"}";

            Entity nearest = FindNearestBuilding(new float3(x, y, z));
            if (nearest == Entity.Null)
                return "{\"error\":\"No building found nearby\"}";

            Entity upgradePrefab = FindPrefabByName(upgradeName);
            if (upgradePrefab == Entity.Null)
                return $"{{\"error\":\"Upgrade prefab '{EscapeJson(upgradeName)}' not found\"}}";

            if (!EntityManager.HasComponent<ObjectData>(upgradePrefab))
                return "{\"error\":\"Prefab is not an upgradeable object\"}";

            var objectData = EntityManager.GetComponentData<ObjectData>(upgradePrefab);
            if (!objectData.m_Archetype.Valid)
                return "{\"error\":\"Upgrade has no valid archetype\"}";

            float3 pos = new float3(x, y, z);

            Entity upgradeEntity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(upgradeEntity, new Game.Objects.Transform
            {
                m_Position = pos,
                m_Rotation = quaternion.identity
            });
            EntityManager.AddComponentData(upgradeEntity, new PrefabRef { m_Prefab = upgradePrefab });
            EntityManager.AddComponentData(upgradeEntity, new Owner { m_Owner = nearest });

            if (EntityManager.HasBuffer<InstalledUpgrade>(nearest))
            {
                EntityManager.GetBuffer<InstalledUpgrade>(nearest)
                    .Add(new InstalledUpgrade { m_Upgrade = upgradeEntity, m_OptionMask = 0 });
            }

            string bldName = m_PrefabSystem?.GetPrefabName(nearest) ?? "unknown";
            Mod.Log.Info($"Installed upgrade '{upgradeName}' on '{bldName}'");
            return $"{{\"building\":\"{EscapeJson(bldName)}\",\"upgrade\":\"{EscapeJson(upgradeName)}\"}}";
        }

        // ── ROAD SPEED COMMANDS ──────────────────────────────────────

        public string SetRoadSpeed(float x, float z, float radius, float speedKmh)
        {
            float3 camPos = new float3(x, 0, z);
            float speedMs = speedKmh / 3.6f;
            int set = 0;

            var edges = m_EdgeQuery.ToEntityArray(Allocator.TempJob);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity e = edges[i];
                if (!EntityManager.HasComponent<Game.Net.Edge>(e)) continue;
                var edge = EntityManager.GetComponentData<Game.Net.Edge>(e);

                if (!EntityManager.Exists(edge.m_Start) ||
                    !EntityManager.HasComponent<Game.Net.Node>(edge.m_Start))
                    continue;

                var node = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start);
                float dist = math.distance(camPos, node.m_Position);
                if (dist > radius) continue;

                if (!EntityManager.HasBuffer<Game.Net.SubLane>(e)) continue;
                var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(e);
                for (int j = 0; j < subLanes.Length; j++)
                {
                    Entity lane = subLanes[j].m_SubLane;
                    if (!EntityManager.Exists(lane)) continue;

                    if (EntityManager.HasComponent<Game.Net.CarLane>(lane))
                    {
                        var carLane = EntityManager.GetComponentData<Game.Net.CarLane>(lane);
                        carLane.m_SpeedLimit = speedMs;
                        EntityManager.SetComponentData(lane, carLane);
                        set++;
                    }
                    else if (EntityManager.HasComponent<Game.Net.TrackLane>(lane))
                    {
                        var trackLane = EntityManager.GetComponentData<Game.Net.TrackLane>(lane);
                        trackLane.m_SpeedLimit = speedMs;
                        EntityManager.SetComponentData(lane, trackLane);
                        set++;
                    }
                }
            }
            edges.Dispose();

            Mod.Log.Info($"SetRoadSpeed on {set} lanes to {speedKmh:F0} km/h");
            return $"{{\"lanes\":{set},\"speedKmh\":{speedKmh:F0}}}";
        }

        // ── HELPERS ────────────────────────────────────────────────────

        private Entity FindPrefabByName(string name)
        {
            if (m_PrefabSystem == null) return Entity.Null;

            var prefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabData>() }
            });

            var entities = prefabQuery.ToEntityArray(Allocator.TempJob);
            Entity bestMatch = Entity.Null;
            string lower = name.ToLowerInvariant();

            for (int i = 0; i < entities.Length; i++)
            {
                string prefabName = m_PrefabSystem.GetPrefabName(entities[i]);
                if (string.IsNullOrEmpty(prefabName)) continue;

                if (string.Equals(prefabName, name, StringComparison.OrdinalIgnoreCase))
                {
                    bestMatch = entities[i];
                    break;
                }

                if (bestMatch == Entity.Null &&
                    prefabName.ToLowerInvariant().Contains(lower))
                {
                    bestMatch = entities[i];
                }
            }

            entities.Dispose();
            return bestMatch;
        }

        private float3 GetCameraPosition()
        {
            if (m_CameraUpdateSystem != null)
                return m_CameraUpdateSystem.position;
            return float3.zero;
        }

        private static ZoneType ParseZoneType(string s)
        {
            return s.ToLowerInvariant() switch
            {
                "residentiallow" or "reslow" or "rl" => new ZoneType { m_Index = 1 },
                "residentialmid" or "resmid" or "rm" => new ZoneType { m_Index = 3 },
                "residentialhigh" or "reshigh" or "rh" => new ZoneType { m_Index = 6 },
                "residential" or "res" => new ZoneType { m_Index = 1 },
                "commercial" or "com" or "c" => new ZoneType { m_Index = 10 },
                "industrial" or "ind" or "i" => new ZoneType { m_Index = 14 },
                "office" or "off" or "o" => new ZoneType { m_Index = 18 },
                "none" or "dezone" => new ZoneType { m_Index = 0 },
                _ => new ZoneType { m_Index = 0 }
            };
        }

        private static PlayerResource ParsePlayerResource(string s)
        {
            return s.ToLowerInvariant() switch
            {
                "electricity" or "power" => PlayerResource.Electricity,
                "healthcare" or "health" => PlayerResource.Healthcare,
                "education" or "edu" => PlayerResource.BasicEducation,
                "secondaryeducation" or "highschool" => PlayerResource.SecondaryEducation,
                "highereducation" or "university" => PlayerResource.HigherEducation,
                "garbage" or "trash" => PlayerResource.Garbage,
                "water" => PlayerResource.Water,
                "mail" or "post" => PlayerResource.Mail,
                "publictransport" or "transport" or "transit" => PlayerResource.PublicTransport,
                "fire" or "firefighting" => PlayerResource.FireResponse,
                "police" => PlayerResource.Police,
                "sewage" => PlayerResource.Sewage,
                "parking" => PlayerResource.Parking,
                _ => PlayerResource.Electricity
            };
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private Entity FindPrefabByComponent<T>() where T : struct, IComponentData
        {
            if (m_PrefabSystem == null) return Entity.Null;

            var query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<T>(),
                    ComponentType.ReadOnly<PrefabData>()
                }
            });

            var entities = query.ToEntityArray(Allocator.TempJob);
            Entity result = entities.Length > 0 ? entities[0] : Entity.Null;
            entities.Dispose();
            return result;
        }

        private Entity FindPrefabByClass<T>() where T : class, IComponentData
        {
            if (m_PrefabSystem == null) return Entity.Null;

            var query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<T>(),
                    ComponentType.ReadOnly<PrefabData>()
                }
            });

            var entities = query.ToEntityArray(Allocator.TempJob);
            Entity result = entities.Length > 0 ? entities[0] : Entity.Null;
            entities.Dispose();
            return result;
        }

        private Entity FindNearestBuilding(float3 pos)
        {
            Entity nearest = Entity.Null;
            float bestDist = float.MaxValue;

            var buildings = m_BuildingQuery.ToEntityArray(Allocator.TempJob);
            for (int i = 0; i < buildings.Length; i++)
            {
                Entity e = buildings[i];
                if (!EntityManager.HasComponent<Game.Buildings.Building>(e)) continue;
                var bld = EntityManager.GetComponentData<Game.Buildings.Building>(e);

                if (EntityManager.Exists(bld.m_RoadEdge) &&
                    EntityManager.HasComponent<Game.Net.Edge>(bld.m_RoadEdge))
                {
                    var edge = EntityManager.GetComponentData<Game.Net.Edge>(bld.m_RoadEdge);
                    if (EntityManager.Exists(edge.m_Start) &&
                        EntityManager.HasComponent<Game.Net.Node>(edge.m_Start))
                    {
                        var node = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start);
                        float dist = math.distance(pos, node.m_Position);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            nearest = e;
                        }
                    }
                }
            }
            buildings.Dispose();
            return nearest;
        }
    }
}
