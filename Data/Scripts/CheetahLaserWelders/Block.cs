using System;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using Sandbox.Common.ObjectBuilders;
using VRage.ObjectBuilders;
using Sandbox.Definitions;
using VRage.Game;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using VRageMath;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using Sandbox.ModAPI.Interfaces;
using Sandbox.Game.Entities;
using System.Linq;
using Sandbox.Game.EntityComponents;
using VRage.Game.ObjectBuilders.Definitions;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Utils;
using Sandbox.Game;
using ProtoBuf;
using Cheetah.Networking;
using SpaceEngineers.Game.ModAPI;

namespace Cheetah.Radars
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_ShipWelder), false, "LargeShipLaserWelder", "SmallShipLaserWelder")]
    public class WelderLogic : BlockLogic { }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_ShipGrinder), false, "LargeShipLaserGrinder", "SmallShipLaserGrinder")]
    public class GrinderLogic : BlockLogic { }

    public class BlockLogic : MyGameLogicComponent
    {
        Sandbox.ModAPI.IMyShipToolBase Tool { get; set; }
        bool IsWelder => Tool is Sandbox.ModAPI.IMyShipWelder;
        bool IsGrinder => Tool is Sandbox.ModAPI.IMyShipGrinder;
        float WorkCoefficient => MyShipGrinderConstants.GRINDER_COOLDOWN_IN_MILISECONDS * 0.001f;
        float GrinderSpeed => MyAPIGateway.Session.GrinderSpeedMultiplier * MyShipGrinderConstants.GRINDER_AMOUNT_PER_SECOND * WorkCoefficient / 4;
        float WelderSpeed => MyAPIGateway.Session.WelderSpeedMultiplier * 2 * WorkCoefficient / 4; // 2 is WELDER_AMOUNT_PER_SECOND from MyShipWelder.cs
        float WelderBoneRepairSpeed => 0.6f * WorkCoefficient; // 0.6f is WELDER_MAX_REPAIR_BONE_MOVEMENT_SPEED from MyShipWelder.cs
        IMyInventory ToolCargo { get; set; }
        HashSet<IMyCubeBlock> OnboardInventoryOwners = new HashSet<IMyCubeBlock>();
        IMyCubeGrid Grid;
        Sandbox.ModAPI.IMyGridTerminalSystem Term;
        MyResourceSinkComponent MyPowerSink;
        float SuppliedPowerRatio => Tool.ResourceSink.SuppliedRatioByType(Electricity);
        public static MyDefinitionId Electricity { get; } = new MyDefinitionId(typeof(MyObjectBuilder_GasProperties), "Electricity");
        /// <summary>
        /// Grid block size, in meters.
        /// </summary>
        float GridBlockSize => Grid.GridSize;
        Vector3I BlockDimensions => (Tool.SlimBlock.BlockDefinition as MyCubeBlockDefinition).Size;
        Vector3D BlockPosition => Tool.GetPosition();
        AutoSet<bool> SyncDistanceMode;
        public bool DistanceMode
        {
            get { return SyncDistanceMode.Get(); }
            set { SyncDistanceMode.Set(value); }
        }
        AutoSet<float> SyncBeamLength;
        public int BeamLength
        {
            get { return (int)SyncBeamLength.Get(); }
            set { SyncBeamLength.Set(value); }
        }
        AutoSet<float> SyncSpeedMultiplier;
        public int SpeedMultiplier
        {
            get { return (int)SyncSpeedMultiplier.Get(); }
            set { SyncSpeedMultiplier.Set(value); }
        }
        public int MinBeamLengthBlocks => 1;
        public int MaxBeamLengthBlocks => Grid.GridSizeEnum == MyCubeSize.Small ? 30 : 8;
		public float MinBeamLengthM => MinBeamLengthBlocks * GridBlockSize;
        public float MaxBeamLengthM => MaxBeamLengthBlocks * GridBlockSize;
        Vector3D BlockForwardEnd => Tool.WorldMatrix.Forward * GridBlockSize * (BlockDimensions.Z) / 2;
        Vector3 LaserEmitterPosition
        {
            get
            {
                var EmitterDummy = Tool.Model.GetDummy("Laser_Emitter");
                return EmitterDummy != null ? EmitterDummy.Matrix.Translation : (Vector3)BlockForwardEnd;
            }
        }
        Vector3D BeamStart => BlockPosition + LaserEmitterPosition;
        Vector3D BeamEnd => BeamStart + Tool.WorldMatrix.Forward * BeamLength * GridBlockSize * SuppliedPowerRatio;
        Color InternalBeamColor { get; } = Color.WhiteSmoke;
        Color ExternalWeldBeamColor { get; } = Color.DeepSkyBlue;
        Color ExternalGrindBeamColor { get; } = Color.IndianRed;
        IMyHudNotification DebugNote;
        System.Diagnostics.Stopwatch Watch = new System.Diagnostics.Stopwatch();
        /// <summary>
        /// In milliseconds
        /// </summary>
        Queue<float> LastRunTimes = new Queue<float>();
        const float RunTimeCacheSize = 120;
        bool RunTimesAvailable => LastRunTimes.Count > 0;
        float AvgRunTime => LastRunTimes.Average();
        float MaxRunTime => LastRunTimes.Max();
        ushort Ticks = 0;
        bool PreviousUseConveyor;

        HashSet<IMySlimBlock> UnbuiltBlocks = new HashSet<IMySlimBlock>();


        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
            Tool = Entity as Sandbox.ModAPI.IMyShipToolBase;
            Grid = Tool.CubeGrid;
            Tool.ResourceSink.SetRequiredInputFuncByType(Electricity, () => PowerConsumptionFunc());
            if (!Tool.HasComponent<MyModStorageComponent>())
            {
                Tool.Storage = new MyModStorageComponent();
                Tool.Components.Add(Tool.Storage);
                SessionCore.DebugWrite($"{Tool.CustomName}.Init()", "Block doesn't have a Storage component!", IsExcessive: false);
            }
        }

        #region Loading stuff
        [ProtoContract]
        public struct Persistent
        {
            [ProtoMember(1)]
            public float BeamLength;
            [ProtoMember(2)]
            public bool DistanceBased;
            [ProtoMember(3)]
            public float SpeedMultiplier;
        }

        public void Load()
        {
            try
            {
                if (MyAPIGateway.Multiplayer.MultiplayerActive && !MyAPIGateway.Multiplayer.IsServer)
                {
                    SyncBeamLength.Ask();
                    SyncDistanceMode.Ask();
                    SyncSpeedMultiplier.Ask();
                    return;
                }
                string Storage = "";
                //if (Tool.Storage.ContainsKey(SessionCore.StorageGuid))
                if (Tool.Storage?.TryGetValue(SessionCore.StorageGuid, out Storage) == true ||
                    MyAPIGateway.Utilities.GetVariable($"settings_{Tool.EntityId}", out Storage))
                {
                    try
                    {
                        Persistent persistent = MyAPIGateway.Utilities.SerializeFromBinary<Persistent>(Convert.FromBase64String(Storage));
                        SyncBeamLength.Set(persistent.BeamLength);
                        SyncDistanceMode.Set(persistent.DistanceBased);
                        SyncSpeedMultiplier.Set(persistent.SpeedMultiplier);
                        SessionCore.DebugWrite($"{Tool.CustomName}.Load()", $"Loaded from storage. Persistent Beamlength: {persistent.BeamLength}; Sync Beamlength: {SyncBeamLength.Get()}");
                    }
                    catch (Exception Scrap)
                    {
                        SessionCore.LogError($"{Tool.CustomName}.Load()", Scrap);
                    }
                }
                else
                {
                    SessionCore.DebugWrite($"{Tool.CustomName}.Load()", "Storage access failed.");
                }
            }
            catch (Exception Scrap)
            {
                SessionCore.LogError($"{Tool.CustomName}.Load().AccessStorage", Scrap);
            }
        }

        public void Save()
        {
            try
            {
                Persistent persistent;
                persistent.BeamLength = SyncBeamLength.Get();
                persistent.DistanceBased = SyncDistanceMode.Get();
                persistent.SpeedMultiplier = SyncSpeedMultiplier.Get();
                string Raw = Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(persistent));
                MyAPIGateway.Utilities.SetVariable($"settings_{Tool.EntityId}", Raw);
                Tool.Storage?.AddOrUpdate(SessionCore.StorageGuid, Raw);
                SessionCore.DebugWrite($"{Tool.CustomName}.Load()", $"Set settings to storage. Saved beamlength: {persistent.BeamLength}");
            }
            catch (Exception Scrap)
            {
                SessionCore.LogError($"{Tool.CustomName}.Save()", Scrap);
            }
        }

        public override void Close()
        {
            try
            {
                if (SessionCore.Debug)
                {
                    DebugNote.Hide();
                    DebugNote.AliveTime = 0;
                    DebugNote = null;
                }
            }
            catch (Exception Scrap)
            {
                SessionCore.LogError($"{Tool.CustomName}.Close().DebugClose", Scrap);
            }
            try
            {
                SessionCore.SaveUnregister(Save);
                SyncBeamLength.GotValueFromServer -= Tool.UpdateVisual;
                SyncDistanceMode.GotValueFromServer -= Tool.UpdateVisual;
                SyncSpeedMultiplier.GotValueFromServer -= Tool.UpdateVisual;
                SyncBeamLength.Close();
                SyncDistanceMode.Close();
                SyncSpeedMultiplier.Close();
            }
            catch (Exception Scrap)
            {
                SessionCore.LogError($"{Tool.CustomName}.Close()", Scrap);
            }
        }
        #endregion

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                if (Tool.CubeGrid.Physics?.Enabled != true || !Networker.Inited)
                {
                    NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
                    if (!Networker.Inited) Networker.Init(SessionCore.ModID);
                    return;
                }
                Term = Grid.GetTerminalSystem();
                ToolCargo = Tool.GetInventory();

                SyncBeamLength = new AutoSet<float>(Tool, "BeamLength", 1, Checker: val => val >= MinBeamLengthBlocks && val <= MaxBeamLengthBlocks);
                SyncDistanceMode = new AutoSet<bool>(Tool, "DistanceBasedMode");
                SyncSpeedMultiplier = new AutoSet<float>(Tool, "SpeedMultiplier", 1, Checker: val => val >= 1 && val <= 4);
                SyncBeamLength.GotValueFromServer += Tool.UpdateVisual;
                SyncDistanceMode.GotValueFromServer += Tool.UpdateVisual;
                SyncSpeedMultiplier.GotValueFromServer += Tool.UpdateVisual;

                CheckInitControls();
                Load();
                
                Tool.AppendingCustomInfo += Tool_AppendingCustomInfo;
                SessionCore.SaveRegister(Save);
                NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_100TH_FRAME;
                DebugNote = MyAPIGateway.Utilities.CreateNotification($"{Tool.CustomName}", int.MaxValue, (IsWelder ? "Blue" : "Red"));
                if (SessionCore.Debug) DebugNote.Show();
                PreviousUseConveyor = Tool.UseConveyorSystem;
            }
            catch { }
        }

        private void Tool_AppendingCustomInfo(Sandbox.ModAPI.IMyTerminalBlock trash, StringBuilder Info)
        {
            Info.Clear();
            Info.AppendLine($"Current Input: {Math.Round(Tool.ResourceSink.RequiredInputByType(Electricity), 2)} MW");
            Info.AppendLine($"Max Required Input: {Math.Round(PowerConsumptionFunc(true), 2)} MW");
            Info.AppendLine($"Performance impact: {(RunTimesAvailable ? Math.Round(AvgRunTime, 4).ToString() : "--")}/{(RunTimesAvailable ? Math.Round(MaxRunTime, 4).ToString() : "--")} ms (avg/max)");
        }

        void CheckInitControls()
        {
            string Message = "Attention! Due to a bug in the game itself, you might not be able to work with these tools via mouse-click.\nIf you run into this issue, you have to use the Toggle switch in terminal or on/off switch on toolbar.\nSorry for inconvenience.";
            if (IsWelder)
            {
                if (!SessionCore.InitedWelderControls)
                {
                    SessionCore.InitWelderControls();
                    MyAPIGateway.Utilities.ShowMessage("Laser Welders", Message);
                }
            }
            else if (IsGrinder)
            {
                if (!SessionCore.InitedGrinderControls)
                {
                    SessionCore.InitGrinderControls();
                    MyAPIGateway.Utilities.ShowMessage("Laser Grinders", Message);
                }
            }
        }

        void Main(int ticks)
        {
            try
            {
                if (Tool.IsToolWorking())
                {
                    Work(ticks);
                    DrawBeam();
                }
                else
                {
                    DebugNote.Text = $"{Tool.CustomName}: idle";
                    UnbuiltBlocks.Clear();
                }
                Tool.RefreshCustomInfo();
                if (SessionCore.Debug) DebugNote.Text = $"{Tool.CustomName} perf. impact: {(RunTimesAvailable ? Math.Round(AvgRunTime, 5).ToString() : "--")}/{(RunTimesAvailable ? Math.Round(MaxRunTime, 5).ToString() : "--")} ms (avg/max)";
            }
            catch (Exception Scrap)
            {
                SessionCore.LogError(Tool.CustomName, Scrap);
            }
        }

        void Aux(int ticks)
        {
            if (Tool.IsToolWorking()) DrawBeam();
        }

        public override void UpdateBeforeSimulation()
        {
            Watch.Start();
            Aux(ticks: 1);
            if (Tool.UseConveyorSystem == true && PreviousUseConveyor == false) BuildInventoryCache();
            Ticks += 1;
            if (Ticks >= SessionCore.WorkSkipTicks)
            {
                Ticks = 0;
                Main(ticks: SessionCore.WorkSkipTicks);
            }
            PreviousUseConveyor = Tool.UseConveyorSystem;

            Watch.Stop();
            if (LastRunTimes.Count >= RunTimeCacheSize) LastRunTimes.Dequeue();
            LastRunTimes.Enqueue(1000 * (Watch.ElapsedTicks / System.Diagnostics.Stopwatch.Frequency));
            Watch.Reset();
        }

        public override void UpdateBeforeSimulation100()
        {
            if (Tool.UseConveyorSystem) BuildInventoryCache(); else OnboardInventoryOwners.Clear();
            if (Tool.UseConveyorSystem && UnbuiltBlocks.Count > 0)
            {
                Dictionary<string, int> TotalMissingList = new Dictionary<string, int>();
                Dictionary<IMySlimBlock, Dictionary<string, int>> MissingPerBlock = new Dictionary<IMySlimBlock, Dictionary<string, int>>();
                UnbuiltBlocks.ReadMissingComponents(TotalMissingList, MissingPerBlock);
                if (!ToolCargo.PullAny(OnboardInventoryOwners, TotalMissingList))
                {
                    PrintMissing(MissingPerBlock);
                }
                UnbuiltBlocks.Clear();
            }
        }

        /// <summary>
        /// Builds cache of accessible inventories on this ship.
        /// </summary>
        /// <param name="Force">Forces to build cache even if UseConveyorSystem == false.</param>
        void BuildInventoryCache(bool Force = false)
        {
            if (!Tool.UseConveyorSystem && !Force) return;
            List<Sandbox.ModAPI.IMyTerminalBlock> trash = new List<Sandbox.ModAPI.IMyTerminalBlock>();
            OnboardInventoryOwners.Clear();
            OnboardInventoryOwners.Add(Tool);
            Func<Sandbox.ModAPI.IMyTerminalBlock, bool> Puller = (Block) =>
            {
                if (!Block.HasPlayerAccess(Tool.OwnerId) || !Block.HasInventory) return false;
                if (Block == Tool) return false;
                foreach (var Inventory in Block.GetInventories())
                {
                    if (Inventory == ToolCargo) return false;
                    if (Inventory.IsConnectedTo(ToolCargo)) OnboardInventoryOwners.Add(Block);
                }
                return false;
            };
            Term.GetBlocksOfType(trash, Puller);
        }

        void Weld(List<IMySlimBlock> Blocks, int ticks = 1)
        {
            Sandbox.ModAPI.IMyShipWelder Welder;
            if (!Tool.IsOfType(out Welder)) return;
            UnbuiltBlocks.Clear();
            if (Blocks.Count == 0) return;
            if (DistanceMode) Blocks = Blocks.OrderByDescending(x => Vector3D.DistanceSquared(x.GetPosition(), Tool.GetPosition())).ToList();
            float SpeedRatio = WelderSpeed / (DistanceMode ? 1 : Blocks.Count) * ticks * SpeedMultiplier;
            float BoneFixSpeed = WelderBoneRepairSpeed * ticks;
            foreach (IMySlimBlock Block in Blocks)
            {
                if (Block.CubeGrid.Physics?.Enabled == true)
                {
                    if (Block.CanContinueBuild(ToolCargo))
                    {
                        Block.MoveItemsToConstructionStockpile(ToolCargo);
                        Block.IncreaseMountLevel(SpeedRatio, Welder.OwnerId, ToolCargo, BoneFixSpeed, false);
                    }
                    else if (Block.HasDeformation) Block.IncreaseMountLevel(SpeedRatio, Welder.OwnerId, ToolCargo, BoneFixSpeed, false);
                    else UnbuiltBlocks.Add(Block);
                }
                else
                {
                    try
                    {
                        var FirstItem = ((MyCubeBlockDefinition)Block.BlockDefinition).Components[0].Definition.Id;
                        if (ToolCargo.PullAny(OnboardInventoryOwners, FirstItem.SubtypeName, 1))
                        {
                            var Projector = ((Block.CubeGrid as MyCubeGrid).Projector as Sandbox.ModAPI.IMyProjector);
                            Projector.Build(Block, 0, Tool.EntityId, false);
                            ToolCargo.RemoveItemsOfType(1, FirstItem);
                        }
                    }
                    catch (Exception Scrap)
                    {
                        SessionCore.LogError(Tool.CustomName + ".WeldProjectorBlockStart", Scrap);
                    }
                }
                if (DistanceMode) break;
            }
            //PrintMissing(UnbuiltBlocks);
        }

        void PrintMissing(Dictionary<IMySlimBlock, Dictionary<string, int>> MissingPerBlock)
        {
            var Player = MyAPIGateway.Session.Player;
            if (Player == null) return;
            if (Player.IdentityId != Tool.OwnerId) return;
            if (Player.GetPosition().DistanceTo(Tool.GetPosition()) > 1000) return;

            StringBuilder Text = new StringBuilder();
            Text.AppendLine($"Performance impact: {(RunTimesAvailable ? Math.Round(AvgRunTime, 4).ToString() : "--")}/{(RunTimesAvailable ? Math.Round(MaxRunTime, 4).ToString() : "--")} ms (avg/max)");
            if (MissingPerBlock.Count == 1)
            {
                IMySlimBlock Block = MissingPerBlock.Keys.First();
                Text.AppendLine($"{Tool.CustomName}: can't proceed to build {Block.BlockDefinition.DisplayNameText}, missing:\n");
                var Missing = MissingPerBlock[Block];
                foreach (var ItemPair in Missing)
                {
                    Text.AppendLine($"{ItemPair.Key}: {ItemPair.Value}");
                }
            }
            else if (UnbuiltBlocks.Count > 1 && MissingPerBlock.Values.Any(x => x.Count > 0))
            {
                Text.AppendLine($"{Tool.CustomName}: can't proceed to build {MissingPerBlock.Count} blocks:\n");
                foreach (IMySlimBlock Block in MissingPerBlock.Keys)
                {
                    var Missing = MissingPerBlock[Block];
                    if (Missing.Count == 0) continue;
                    Text.AppendLine($"{Block.BlockDefinition.DisplayNameText}: missing:");
                    foreach (var ItemPair in Missing)
                    {
                        Text.AppendLine($"{ItemPair.Key}: {ItemPair.Value}");
                    }
                    Text.AppendLine();
                }
            }
            Text.RemoveTrailingNewlines();
            IMyHudNotification hud = MyAPIGateway.Utilities.CreateNotification(Text.ToString(), (int)Math.Ceiling(SessionCore.TickLengthMs * 101), "Red"); // Adding 1 excess tick is needed, otherwise notification can flicker
            hud.Show();
        }

        void Grind(List<IMySlimBlock> Blocks, int ticks = 1)
        {
            Sandbox.ModAPI.IMyShipGrinder Grinder;
            if (!Tool.IsOfType(out Grinder)) return;
            if (Blocks.Count == 0) return;
            if (DistanceMode) Blocks = Blocks.OrderBy(x => Vector3D.DistanceSquared(x.GetPosition(), Tool.GetPosition())).ToList();
            float SpeedRatio = GrinderSpeed / (DistanceMode ? 1 : Blocks.Count) * ticks * SpeedMultiplier;
            foreach (IMySlimBlock Block in Blocks)
            {
                Block.MoveItemsFromConstructionStockpile(ToolCargo);
                Block.DecreaseMountLevel(SpeedRatio, ToolCargo, useDefaultDeconstructEfficiency: true);
                if (Block.FatBlock?.IsFunctional == false && Block.FatBlock?.HasInventory == true)
                {
                    foreach (var Inventory in Block.FatBlock.GetInventories())
                    {
                        if (Inventory.CurrentVolume == VRage.MyFixedPoint.Zero) continue;
                        foreach (var Item in Inventory.GetItems())
                        {
                            var Amount = Inventory.ComputeAmountThatFits(Item);
                            ToolCargo.TransferItemFrom(Inventory, (int)Item.ItemId, null, null, Amount, false);
                        }
                    }
                }
                if (Block.IsFullyDismounted) Block.CubeGrid.RazeBlock(Block.Position);
                if (DistanceMode) break;
            }
        }

        void Work(int ticks = 1)
        {
            if (!MyAPIGateway.Multiplayer.IsServer) return;
            LineD WeldRay = new LineD(BeamStart, BeamEnd);
            List<IHitInfo> Hits = new List<IHitInfo>();
            List<MyLineSegmentOverlapResult<MyEntity>> Overlaps = new List<MyLineSegmentOverlapResult<MyEntity>>();
            MyGamePruningStructure.GetTopmostEntitiesOverlappingRay(ref WeldRay, Overlaps);

            EntityByDistanceSorter GridSorter = new EntityByDistanceSorter(Tool.GetPosition());

            HashSet<IMyCubeGrid> Grids = new HashSet<IMyCubeGrid>();
            HashSet<IMyCharacter> Characters = new HashSet<IMyCharacter>();
            HashSet<IMyFloatingObject> Flobjes = new HashSet<IMyFloatingObject>();
            Overlaps.Select(x => x.Element as IMyEntity).SortByType(Grids, Characters, Flobjes);

            foreach (IMyCharacter Char in Characters)
            {
                Char.DoDamage(GrinderSpeed * ticks / 5, MyDamageType.Grind, true, null, Tool.EntityId);
            }

            foreach (IMyCubeGrid Grid in Grids)
            {
                if (Grid == Tool.CubeGrid) continue;
                try
                {
                    Vector3I? Inflate = null;
                    if (!DistanceMode && Grid.GridSizeEnum == MyCubeSize.Small) Inflate = Vector3I.One * 5;

                    if (IsWelder)
                    {
                        var Blocks = Grid.GetBlocksOnRay(WeldRay.From, WeldRay.To, collect: x => x.IsWeldable() || x.IsProjectable());
                        Weld(Blocks, ticks);
                    }
                    else if (IsGrinder)
                    {
                        var Blocks = Grid.GetBlocksOnRay(WeldRay.From, WeldRay.To, collect: x => x.IsGrindable());
                        Grind(Blocks, ticks);
                    }
                }
                catch (Exception Scrap)
                {
                    SessionCore.LogError(Grid.DisplayName, Scrap);
                }
            }

            foreach (IMyFloatingObject Flobj in Flobjes)
            {
                ToolCargo.PickupItem(Flobj);
            }
        }

        void DrawBeam()
        {
            if (MyAPIGateway.Session.Player == null) return;
            var Internal = InternalBeamColor.ToVector4();
            var External = Vector4.Zero;
            if (IsWelder) External = ExternalWeldBeamColor.ToVector4();
            if (IsGrinder) External = ExternalGrindBeamColor.ToVector4();
            var BeamStart = this.BeamStart;
            var BeamEnd = this.BeamEnd;
            MySimpleObjectDraw.DrawLine(BeamStart, BeamEnd, MyStringId.GetOrCompute("WeaponLaser"), ref Internal, 0.1f);
            MySimpleObjectDraw.DrawLine(BeamStart, BeamEnd, MyStringId.GetOrCompute("WeaponLaser"), ref External, 0.2f);
        }

        public override void UpdatingStopped()
        {
            
        }

        float PowerConsumptionFunc(bool Test = false)
        {
            try
            {
                if (!Test && !Tool.IsToolWorking()) return 0;
                if (SpeedMultiplier <= 1)
                    return (float)Math.Pow(1.2, BeamLength * GridBlockSize);
                else
                    return (float)Math.Pow(1.2, BeamLength * GridBlockSize) + ((float)Math.Pow(1.2, BeamLength * GridBlockSize) * SpeedMultiplier-1 * 0.8f);
            }
            catch
            {
                return 0;
            }
        }
    }
}