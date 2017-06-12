using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VRage.Utils;

namespace Cheetah.Radars
{
    static class Controls
    {
        public static IMyTerminalControlSlider LaserBeam<T>() where T: IMyTerminalBlock
        {
            IMyTerminalControlSlider LaserBeam = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, T>("BeamLength");
            LaserBeam.Title = MyStringId.GetOrCompute("Beam Length");
            LaserBeam.Tooltip = MyStringId.GetOrCompute("Sets the laser beam's length.");
            LaserBeam.SupportsMultipleBlocks = true;
            LaserBeam.Enabled = HasBlockLogic;
            LaserBeam.Visible = HasBlockLogic;
            LaserBeam.SetLimits(Block => BlockReturn(Block, x => x.MinBeamLengthBlocks), Block => BlockReturn(Block, x => x.MaxBeamLengthBlocks));
            LaserBeam.Getter = (Block) => BlockReturn(Block, x => x.BeamLength);
            LaserBeam.Setter = (Block, NewLength) => BlockAction(Block, x => x.BeamLength = (int)NewLength);
            LaserBeam.Writer = (Block, Info) => Info.Append($"{BlockReturn(Block, x => x.BeamLength)} blocks");
            return LaserBeam;
        }

        public static IMyTerminalControlSlider SpeedMultiplier<T>() where T : IMyTerminalBlock
        {
            IMyTerminalControlSlider SpeedMultiplier = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, T>("SpeedMultiplier");
            SpeedMultiplier.Title = MyStringId.GetOrCompute("Speed Multiplier");
            SpeedMultiplier.Tooltip = MyStringId.GetOrCompute("Allows to increase tool's speed at the cost of power usage.\nThis is more efficient than piling on multiple tools.");
            SpeedMultiplier.SupportsMultipleBlocks = true;
            SpeedMultiplier.Enabled = HasBlockLogic;
            SpeedMultiplier.Visible = HasBlockLogic;
            SpeedMultiplier.SetLimits(1, 4);
            SpeedMultiplier.Getter = (Block) => BlockReturn(Block, x => x.SpeedMultiplier);
            SpeedMultiplier.Setter = (Block, NewSpeed) => BlockAction(Block, x => x.SpeedMultiplier = (int)NewSpeed);
            SpeedMultiplier.Writer = (Block, Info) => Info.Append($"x{BlockReturn(Block, x => x.SpeedMultiplier)}");
            return SpeedMultiplier;
        }

        public static IMyTerminalControlCheckbox DistanceMode<T>() where T : IMyTerminalBlock
        {
            IMyTerminalControlCheckbox DistanceMode = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, T>("DistanceMode");
            DistanceMode.SupportsMultipleBlocks = true;
            DistanceMode.Enabled = HasBlockLogic;
            DistanceMode.Visible = HasBlockLogic;
            DistanceMode.Getter = (Block) => BlockReturn(Block, x => x.DistanceMode);
            DistanceMode.Setter = (Block, NewMode) => BlockAction(Block, x => x.DistanceMode = NewMode);
            return DistanceMode;
        }

        public static bool HasBlockLogic(IMyTerminalBlock Block)
        {
            try
            {
                return Block.HasComponent<BlockLogic>();
            }
            catch (Exception Scrap)
            {
                SessionCore.LogError("IsOurBlock", Scrap);
                return false;
            }
        }

        public static void BlockAction(IMyTerminalBlock Block, Action<BlockLogic> Action)
        {
            try
            {
                BlockLogic Logic;
                if (!Block.TryGetComponent(out Logic)) return;
                Action(Logic);
            }
            catch (Exception Scrap)
            {
                SessionCore.LogError("BlockAction", Scrap);
                return;
            }
        }

        public static T BlockReturn<T>(IMyTerminalBlock Block, Func<BlockLogic, T> Getter, T Default = default(T))
        {
            try
            {
                BlockLogic Logic;
                if (!Block.TryGetComponent(out Logic)) return Default;
                return Getter(Logic);
            }
            catch (Exception Scrap)
            {
                SessionCore.LogError("BlockReturn", Scrap);
                return Default;
            }
        }
    }
}
