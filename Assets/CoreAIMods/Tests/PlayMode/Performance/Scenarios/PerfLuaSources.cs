using System;
using System.Globalization;
using System.Text;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// The Lua the reference scenes run. Written the way a Roblox author writes it (RunService.Heartbeat,
    /// Vector3.new per write) so the measured cost is the cost an ordinary mod pays, not a tuned best case.
    /// </summary>
    public static class PerfLuaSources
    {
        /// <summary>Mod id prefix of the Heartbeat mover mods (one mod per handler).</summary>
        public const string MoverModIdPrefix = "perf-heartbeat-movers-";

        /// <summary>
        /// Name prefix of the moved Parts. They are parented straight to workspace: the 16,384-Part scene
        /// sits exactly at the registered-instance ceiling, so it has no room for a folder.
        /// </summary>
        public const string MoverNamePrefix = "PerfMover";

        /// <summary>
        /// Source of mover mod <paramref name="handlerIndex"/>: it creates <paramref name="moverCount"/>
        /// anchored Parts in its main chunk and moves every one of them from one Heartbeat handler.
        /// </summary>
        public static string BuildMoverMod(int handlerIndex, int moverCount)
        {
            if (handlerIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(handlerIndex));
            }

            if (moverCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(moverCount));
            }

            string row = handlerIndex.ToString(CultureInfo.InvariantCulture);
            StringBuilder source = new();
            source.Append("local RunService = game:GetService('RunService')\n");
            source.Append("local row = ").Append(row).Append('\n');
            source.Append("local count = ").Append(moverCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            source.Append("local movers = {}\n");
            source.Append("for i = 1, count do\n");
            source.Append("  local p = Instance.new('Part')\n");
            source.Append("  p.Name = '").Append(MoverNamePrefix).Append(row).Append("_' .. i\n");
            source.Append("  p.Anchored = true\n");
            source.Append("  p.Size = Vector3.new(2, 2, 2)\n");
            source.Append("  p.Position = Vector3.new(i * 3, 20 + row * 4, -20)\n");
            source.Append("  p.Parent = workspace\n");
            source.Append("  movers[i] = p\n");
            source.Append("end\n");
            source.Append("local t = 0\n");
            source.Append("RunService.Heartbeat:Connect(function(dt)\n");
            source.Append("  t = t + dt\n");
            source.Append("  for i = 1, count do\n");
            source.Append("    movers[i].Position = Vector3.new(i * 3, 20 + row * 4 + math.sin(t + i * 0.1) * 2, -20)\n");
            source.Append("  end\n");
            source.Append("end)\n");
            return source.ToString();
        }
    }
}
