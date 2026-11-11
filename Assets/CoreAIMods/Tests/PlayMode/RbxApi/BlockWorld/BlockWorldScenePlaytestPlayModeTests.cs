#if UNITY_EDITOR && COREAI_HAS_INPUT_SYSTEM
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi.BlockWorld
{
    /// <summary>
    /// The Block World scene as a player tries it: sand that falls when its support is broken (a capture sequence), and
    /// torches on a floor and on the four faces of a wall, each seen from four sides.
    /// </summary>
    public sealed partial class BlockWorldScenePlayModeTests
    {
        private int _playtestProbes;

        /// <summary>A cell three blocks ahead of the player along the spawn's heading, and its ground's top row.</summary>
        private (int X, int Z, int Ground) CellAhead(int blocks)
        {
            RbxVector3 root = RootPosition(Humanoid());
            RbxVector3 look = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawnLook");
            RbxVector3 flat = new RbxVector3(look.X, 0f, look.Z).Unit;
            int x = Mathf.FloorToInt((root.X + flat.X * blocks * Cell) / Cell);
            int z = Mathf.FloorToInt((root.Z + flat.Z * blocks * Cell) / Cell);
            TerrainMaterials.TryGetSlot("Mud", out byte trunk);
            TerrainMaterials.TryGetSlot("LeafyGrass", out byte leaves);
            return (x, z, GroundTop(Terrain(), x, z, trunk, leaves));
        }

        /// <summary>Runs a probe as the player and unloads it, so a long audit stays under the per-actor mod quota.</summary>
        private void RunOnce(string prefix, string code)
        {
            string id = prefix + ++_playtestProbes;
            RunAsPlayer(id, code);
            Session().CurrentConcreteRuntime.UnloadMod(PlayerActor(), id);
        }

        private void FillBox(int x0, int y0, int z0, int x1, int y1, int z1, string material)
        {
            RunOnce("test_box_", "local terrain = workspace.Terrain "
                                                        + "terrain:FillRegion(Region3.new(terrain:CellCornerToWorld(" + x0 + ", " + y0
                                                        + ", " + z0 + "), terrain:CellCornerToWorld(" + (x1 + 1) + ", " + (y1 + 1)
                                                        + ", " + (z1 + 1) + ")), 4, Enum.Material." + material + ")");
        }

        /// <summary>The row of the cell the player's feet are in.</summary>
        private int FeetRow()
        {
            return Mathf.FloorToInt((RootY() - 3f) / Cell);
        }

        /// <summary>
        /// Clears a 3 x 3 patch round cell (x, z) to a level floor at <paramref name="floor"/> (Grass) with four rows of
        /// air above it, so a check starts on fresh, flat land.
        /// </summary>
        private void LevelPatch(int x, int z, int floor)
        {
            FillBox(x - 1, floor, z - 1, x + 1, floor, z + 1, "Grass");
            FillBox(x - 1, floor + 1, z - 1, x + 1, floor + 4, z + 1, "Air");
        }

        /// <summary>Stands the player on cell (x, floor, z): its feet a tenth of a stud over the floor's top.</summary>
        private void StandOnCell(int x, int floor, int z)
        {
            RunOnce("test_stand_", "local c = game:GetService('Players'):GetPlayers()[1].Character "
                                   + "c:PivotTo(CFrame.new(" + Lua((x + 0.5f) * Cell) + ", " + Lua((floor + 1) * Cell + 4f) + ", "
                                   + Lua((z + 0.5f) * Cell) + ")) local low = math.huge "
                                   + "for _, p in ipairs(c:GetChildren()) do if p:IsA('BasePart') then "
                                   + "low = math.min(low, p.Position.Y - p.Size.Y / 2) end end "
                                   + "c:PivotTo(c:GetPivot() + Vector3.new(0, " + Lua((floor + 1) * Cell + 0.1f) + " - low, 0)) "
                                   + "pcall(function() c.HumanoidRootPart.AssemblyLinearVelocity = Vector3.zero end)");
        }

        private void FillCells(int x, int z, int fromRow, int toRow, string material)
        {
            RunOnce("test_fill_", "local terrain = workspace.Terrain "
                                                         + "for y = " + fromRow + ", " + toRow + " do "
                                                         + "terrain:FillBlock(CFrame.new(terrain:CellCenterToWorld(" + x + ", y, " + z
                                                         + ")), Vector3.new(4, 4, 4), Enum.Material." + material + ") end");
        }

        // ---- falling sand ---------------------------------------------------------------------------------------------

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator BlockWorldScene_SandFalls_WhenItsSupportIsBroken_ACaptureSequence()
        {
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            yield return SetClock(12d);
            // WHY a cleared, level box with the player beside it: the pillar is seen whole against the sky from the
            // side, and the player stands inside the land the falling mod watches.
            (int x, int z, int ground) = CellAhead(6);
            FillBox(x - 4, ground, z - 4, x + 4, ground, z + 4, "Grass");
            FillBox(x - 4, ground + 1, z - 4, x + 4, ground + 8, z + 4, "Air");
            FillCells(x, z, ground + 1, ground + 3, "Rock");
            FillCells(x, z, ground + 4, ground + 6, "Sand");
            RbxVector3 look = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawnLook");
            RbxVector3 flatLook = new RbxVector3(look.X, 0f, look.Z).Unit;
            StandOnCell(x - Mathf.RoundToInt(flatLook.X * 3f), ground, z - Mathf.RoundToInt(flatLook.Z * 3f));
            yield return Frames(40);

            RbxVector3 side = new RbxVector3(look.Z, 0f, -look.X).Unit;
            RbxVector3 middle = new((x + 0.5f) * Cell, (ground + 3.5f) * Cell, (z + 0.5f) * Cell);
            yield return Aim(middle + side * 15f + new RbxVector3(0f, 1f, 0f), middle);
            Shoot("sand_fall_0_before.png");
            bool restedBefore = Terrain().Store.GetMaterial(x, ground + 4, z) == Slot("Sand");

            // WHY all three at once: what is measured is the fall, not how fast a player breaks three blocks.
            FillCells(x, z, ground + 1, ground + 3, "Air");
            float start = Time.time;
            int shot = 1;
            List<string> rows = new();
            while (shot <= 5 && Time.time - start < 3f)
            {
                if (Time.time - start >= shot * 0.1f)
                {
                    Shoot("sand_fall_" + shot + "_" + shot * 100 + "ms.png");
                    rows.Add(shot * 100 + " ms: lowest sand at row " + LowestSand(x, z, ground));
                    shot++;
                }

                yield return null;
            }

            yield return Frames(60);
            Shoot("sand_fall_6_landed.png");
            bool landed = true;
            for (int y = ground + 1; y <= ground + 3; y++)
            {
                landed &= Terrain().Store.GetMaterial(x, y, z) == Slot("Sand");
            }

            for (int y = ground + 4; y <= ground + 6; y++)
            {
                landed &= Terrain().Store.GetMaterial(x, y, z) == TerrainMaterials.Air;
            }

            Record("sand: a pillar of three on three Rock at cell (" + x + ", " + z + "), ground row " + ground
                   + "; the Rock removed at once; " + string.Join(", ", rows) + "; landed " + landed);
            Expect(restedBefore, "sand on a block rests");
            Expect(landed, "the three sand blocks fell three rows onto the ground, the rows above them empty");
            Report();
        }

        private int LowestSand(int x, int z, int ground)
        {
            for (int y = ground + 1; y <= ground + 6; y++)
            {
                if (Terrain().Store.GetMaterial(x, y, z) == Slot("Sand"))
                {
                    return y - ground;
                }
            }

            return -1;
        }

        private static byte Slot(string material)
        {
            TerrainMaterials.TryGetSlot(material, out byte slot);
            return slot;
        }

        // ---- torches from every side ------------------------------------------------------------------------------------

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator BlockWorldScene_TorchesOnAFloorAndOnFourWalls_FromEverySide()
        {
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            yield return SetClock(19.5d);
            // WHY a cleared, level box: on hilly ground with trees a face can be hidden from the camera that points the
            // crosshair at it; the player stands beside the box so every face is within reach.
            (int x, int z, int ground) = CellAhead(6);
            FillBox(x - 3, ground, z - 3, x + 3, ground, z + 3, "Grass");
            FillBox(x - 3, ground + 1, z - 3, x + 3, ground + 6, z + 3, "Air");
            FillCells(x, z, ground + 1, ground + 1, "Rock");
            RbxVector3 look = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawnLook");
            RbxVector3 flatLook = new RbxVector3(look.X, 0f, look.Z).Unit;
            StandOnCell(x - Mathf.RoundToInt(flatLook.X * 2f), ground, z - Mathf.RoundToInt(flatLook.Z * 2f));
            yield return Frames(40);
            yield return Tap(_keyboard.digit9Key);

            RbxVector3 side = new RbxVector3(look.Z, 0f, -look.X).Unit;
            RbxVector3 wall = new((x + 0.5f) * Cell, (ground + 1.5f) * Cell, (z + 0.5f) * Cell);
            List<(string Name, RbxVector3 At)> torches = new();

            // The floor: two cells to the side of the wall block, on the level floor.
            int floorX = x + Mathf.RoundToInt(side.X * 2f);
            int floorZ = z + Mathf.RoundToInt(side.Z * 2f);
            RbxVector3 floorFace = new((floorX + 0.5f) * Cell, (ground + 1) * Cell, (floorZ + 0.5f) * Cell);
            yield return Aim(floorFace + new RbxVector3(0f, 6f, 0f) - look * 3f, floorFace);
            yield return Click(_mouse.rightButton);
            torches.Add(("floor", floorFace + new RbxVector3(0f, 1.5f, 0f)));

            foreach ((string name, RbxVector3 normal) in new[]
                     {
                         ("east", new RbxVector3(1f, 0f, 0f)), ("west", new RbxVector3(-1f, 0f, 0f)),
                         ("north", new RbxVector3(0f, 0f, 1f)), ("south", new RbxVector3(0f, 0f, -1f))
                     })
            {
                RbxVector3 face = wall + normal * (Cell / 2f);
                yield return Aim(face + normal * 6f + new RbxVector3(0f, 0.5f, 0f), face);
                yield return Click(_mouse.rightButton);
                torches.Add((name, face + normal * 0.8f + new RbxVector3(0f, 0.3f, 0f)));
            }

            yield return Tap(_keyboard.digit1Key);
            object placed = Workspace().GetAttribute("BlockWorldTorches");
            foreach ((string name, RbxVector3 at) in torches)
            {
                int view = 0;
                foreach (RbxVector3 from in new[]
                         {
                             new RbxVector3(5f, 1f, 0f), new RbxVector3(0f, 1f, 5f), new RbxVector3(-5f, 1f, 0f),
                             new RbxVector3(0f, 1f, -5f)
                         })
                {
                    yield return Aim(ClearView(at, at + from), at);
                    yield return Frames(3);
                    Shoot("torch_" + name + "_" + view++ + ".png");
                }
            }

            Record("torches: " + placed + " put on a floor and the four faces of a wall block at cell (" + x + ", "
                   + (ground + 1) + ", " + z + ")");
            Expect(System.Convert.ToInt32(placed ?? 0) == 5, "one torch on the floor and one on each face of the wall");
            Report();
        }

        // ---- a playtest audit against a block game's basics ------------------------------------------------------------

        private IEnumerator Hold(UnityEngine.InputSystem.Controls.ButtonControl button, float seconds)
        {
            Press(button);
            float until = Time.time + seconds;
            while (Time.time < until)
            {
                yield return null;
            }

            Release(button);
            yield return Frames(2);
        }

        private float RootY()
        {
            return RootPosition(Humanoid()).Y;
        }

        private RbxVector3 Eye()
        {
            return EyePosition();
        }

        private void PutPlayer(RbxVector3 at)
        {
            RunOnce("test_put_player_", "local c = game:GetService('Players'):GetPlayers()[1].Character "
                                                                + "c:PivotTo(CFrame.new(" + Lua(at.X) + ", " + Lua(at.Y) + ", "
                                                                + Lua(at.Z) + ")) "
                                                                + "pcall(function() c.HumanoidRootPart.AssemblyLinearVelocity = Vector3.zero end)");
        }

        private static double Number(object value)
        {
            return value == null ? 0d : System.Convert.ToDouble(value);
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator BlockWorldScene_APlaytestAudit_AgainstABlockGamesBasics()
        {
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            yield return SetClock(12d);
            System.Text.StringBuilder audit = new();
            RbxVector3 look = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawnLook");
            RbxVector3 flat = new RbxVector3(look.X, 0f, look.Z).Unit;
            RbxVector3 side = new RbxVector3(flat.Z, 0f, -flat.X);
            RbxVector3 home = RootPosition(Humanoid());
            Set(_mouse.position, new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));

            // 1. Breaking and placing held down: the time between the first and the second block.
            int feet = FeetRow();
            int bx = Mathf.FloorToInt(home.X / Cell + flat.X * 3f);
            int bz = Mathf.FloorToInt(home.Z / Cell + flat.Z * 3f);
            LevelPatch(bx, bz, feet - 1);
            yield return Frames(5);
            RbxVector3 groundFace = new((bx + 0.5f) * Cell, feet * Cell, (bz + 0.5f) * Cell);
            yield return Aim(Eye(), groundFace);
            List<float> breaks = new();
            double brokenSeen = Number(Workspace().GetAttribute("BlockWorldBroken"));
            Press(_mouse.leftButton);
            float pressed = Time.time;
            while (Time.time - pressed < 1.5f && breaks.Count < 2)
            {
                yield return null;
                double now = Number(Workspace().GetAttribute("BlockWorldBroken"));
                if (now > brokenSeen)
                {
                    brokenSeen = now;
                    breaks.Add(Time.time - pressed);
                }
            }

            Release(_mouse.leftButton);
            yield return Frames(5);
            FillCells(bx, bz, feet - 3, feet - 1, "Grass");
            yield return Frames(5);
            yield return Aim(Eye(), groundFace);
            List<float> places = new();
            double placedSeen = Number(Workspace().GetAttribute("BlockWorldPlaced"));
            Press(_mouse.rightButton);
            pressed = Time.time;
            while (Time.time - pressed < 1.5f && places.Count < 2)
            {
                yield return null;
                double now = Number(Workspace().GetAttribute("BlockWorldPlaced"));
                if (now > placedSeen)
                {
                    placedSeen = now;
                    places.Add(Time.time - pressed);
                }
            }

            Release(_mouse.rightButton);
            yield return Frames(5);
            audit.Append("1 held left: blocks broken at ").Append(string.Join(", ", breaks.Select(t => t.ToString("0.00"))))
                .Append(" s; held right: placed at ").Append(string.Join(", ", places.Select(t => t.ToString("0.00"))))
                .Append(" s | ");

            // 2. Placing at your own feet (looking straight down) is refused.
            yield return Aim(Eye(), Eye() - new RbxVector3(0f, 8f, 0f));
            double placedBefore = Number(Workspace().GetAttribute("BlockWorldPlaced"));
            yield return Click(_mouse.rightButton);
            audit.Append("2 placing into yourself: ").Append(Number(Workspace().GetAttribute("BlockWorldPlaced")) - placedBefore)
                .Append(" placed, notice '").Append(Workspace().GetAttribute("BlockWorldNotice")).Append("' | ");

            // 3. Jumping on flat ground, and onto a step one block high.
            float before = RootY();
            float apex = before;
            Press(_keyboard.spaceKey);
            for (int frame = 0; frame < 60; frame++)
            {
                yield return null;
                apex = Mathf.Max(apex, RootY());
            }

            Release(_keyboard.spaceKey);
            yield return Frames(60);
            // WHY fresh, level land: the step stands one block above the very floor the player walks on.
            int stepFloor = FeetRow() - 1;
            int px = Mathf.FloorToInt(home.X / Cell + side.X * 4f);
            int pz = Mathf.FloorToInt(home.Z / Cell + side.Z * 4f);
            LevelPatch(px, pz, stepFloor);
            int sx = px + Mathf.RoundToInt(flat.X * 2f);
            int sz = pz + Mathf.RoundToInt(flat.Z * 2f);
            LevelPatch(px + Mathf.RoundToInt(flat.X), pz + Mathf.RoundToInt(flat.Z), stepFloor);
            FillCells(sx, sz, stepFloor, stepFloor + 1, "Rock");
            FillCells(sx, sz, stepFloor + 2, stepFloor + 5, "Air");
            StandOnCell(px, stepFloor, pz);
            yield return Frames(40);
            RbxVector3 stepAt = new((sx + 0.5f) * Cell, (stepFloor + 1.5f) * Cell, (sz + 0.5f) * Cell);
            float startY = RootY();
            yield return Aim(Eye(), new RbxVector3(stepAt.X, Eye().Y, stepAt.Z));
            Press(_keyboard.wKey);
            yield return Frames(15);
            Press(_keyboard.spaceKey);
            float highest = startY;
            for (int frame = 0; frame < 90; frame++)
            {
                yield return null;
                highest = Mathf.Max(highest, RootY());
            }

            Release(_keyboard.spaceKey);
            Release(_keyboard.wKey);
            yield return Frames(30);
            RbxVector3 afterStep = RootPosition(Humanoid());
            yield return Aim(afterStep + new RbxVector3(0f, 6f, 0f) - flat * 10f + side * 4f, afterStep);
            yield return ShootWithUi("audit_3_step.png");
            audit.Append("3 a jump on flat ground rises ").Append((apex - before).ToString("0.0"))
                .Append(" studs; walking at a one-block step with jump held rose ").Append((highest - startY).ToString("0.0"))
                .Append(" and ended ").Append((afterStep.Y - startY).ToString("0.0")).Append(" studs up (a block is 4) | ");

            // 4. Walking into a wall, then along it.
            int wallFloor = FeetRow() - 1;
            int qx = Mathf.FloorToInt(home.X / Cell - side.X * 4f);
            int qz = Mathf.FloorToInt(home.Z / Cell - side.Z * 4f);
            LevelPatch(qx, qz, wallFloor);
            LevelPatch(qx + Mathf.RoundToInt(flat.X), qz + Mathf.RoundToInt(flat.Z), wallFloor);
            int wx = qx + Mathf.RoundToInt(flat.X * 2f);
            int wz = qz + Mathf.RoundToInt(flat.Z * 2f);
            FillBox(wx - 1, wallFloor + 1, wz - 1, wx + 1, wallFloor + 3, wz + 1, "Rock");
            StandOnCell(qx, wallFloor, qz);
            yield return Frames(40);
            yield return Aim(Eye(), Eye() + flat * 10f);
            yield return Hold(_keyboard.wKey, 1f);
            RbxVector3 atWall = RootPosition(Humanoid());
            yield return Hold(_keyboard.aKey, 1f);
            RbxVector3 alongWall = RootPosition(Humanoid());
            audit.Append("4 into a wall then along it: moved ").Append((alongWall - atWall).Magnitude.ToString("0.0"))
                .Append(" studs sideways in 1 s, height change ").Append((alongWall.Y - atWall.Y).ToString("0.0")).Append(" | ");

            // 5. Water: the nearest surface water, falling in, and the view under it.
            TerrainMaterials.TryGetSlot("Water", out byte water);
            Vector3Int pool = default;
            bool hasWater = false;
            int hx = Mathf.FloorToInt(home.X / Cell);
            int hz = Mathf.FloorToInt(home.Z / Cell);
            for (int radius = 1; radius <= 30 && !hasWater; radius++)
            {
                for (int dx = -radius; dx <= radius && !hasWater; dx++)
                {
                    foreach (int dz in new[] { -radius, radius })
                    {
                        for (int y = 15; y > -16; y--)
                        {
                            byte material = Terrain().Store.GetMaterial(hx + dx, y, hz + dz);
                            if (material == TerrainMaterials.Air)
                            {
                                continue;
                            }

                            if (material == water && Terrain().Store.GetMaterial(hx + dx, y - 1, hz + dz) == water)
                            {
                                pool = new Vector3Int(hx + dx, y, hz + dz);
                                hasWater = true;
                            }

                            break;
                        }

                        if (hasWater)
                        {
                            break;
                        }
                    }
                }
            }

            if (hasWater)
            {
                RbxVector3 surface = new((pool.x + 0.5f) * Cell, (pool.y + 1) * Cell, (pool.z + 0.5f) * Cell);
                PutPlayer(surface + new RbxVector3(0f, 4f, 0f));
                yield return Frames(150);
                RbxVector3 inWater = RootPosition(Humanoid());
                string state = Humanoid().GetState().ToString();
                RbxVector3 under = surface - new RbxVector3(0f, 2f, 0f);
                yield return Aim(under, under + flat * 10f);
                Color[] view = Shoot("audit_5_underwater.png");
                Color mean = Mean(view, new RectInt(0, 0, ShotWidth, ShotHeight));
                audit.Append("5 water at cell ").Append(pool).Append(": after 2.5 s in it the root is ")
                    .Append((inWater.Y - surface.Y).ToString("0.0")).Append(" studs from the surface, state ").Append(state)
                    .Append("; the view under water has mean ").Append(mean).Append(" | ");
            }
            else
            {
                audit.Append("5 water: none within 30 cells | ");
            }

            // 6. Flying: Space twice quickly, then from a hover Space held 0.5 s and, from a hover again, Left Shift 0.5 s.
            PutPlayer(home + new RbxVector3(0f, 1f, 0f));
            yield return Frames(40);
            yield return Aim(Eye(), Eye() + flat * 10f);
            Press(_keyboard.spaceKey);
            yield return Frames(3);
            Release(_keyboard.spaceKey);
            yield return Frames(3);
            Press(_keyboard.spaceKey);
            yield return Frames(3);
            Release(_keyboard.spaceKey);
            yield return Frames(40);
            string flying = Humanoid().GetState().ToString();
            bool isFlying = Humanoid().IsFlying;
            float hoverA = RootY();
            yield return Hold(_keyboard.spaceKey, 0.5f);
            yield return Frames(40);
            float hoverB = RootY();
            yield return Hold(_keyboard.leftShiftKey, 0.5f);
            yield return Frames(40);
            float hoverC = RootY();
            yield return ShootWithUi("audit_6_flying.png");
            Press(_keyboard.spaceKey);
            yield return Frames(3);
            Release(_keyboard.spaceKey);
            yield return Frames(3);
            Press(_keyboard.spaceKey);
            yield return Frames(3);
            Release(_keyboard.spaceKey);
            yield return Frames(90);
            audit.Append("6 flying: after Space twice the state is ").Append(flying).Append(" (flying ").Append(isFlying)
                .Append("); from a hover Space held 0.5 s rose ").Append((hoverB - hoverA).ToString("0.0"))
                .Append(", from a hover Left Shift held 0.5 s sank ").Append((hoverB - hoverC).ToString("0.0")).Append(" | ");

            // 7. The hotbar: a number key and the mouse wheel.
            yield return Tap(_keyboard.digit3Key);
            object three = Workspace().GetAttribute("BlockWorldSelected");
            Set(_mouse.scroll, new Vector2(0f, -120f));
            yield return Frames(3);
            Set(_mouse.scroll, Vector2.zero);
            yield return Frames(3);
            object wheel = Workspace().GetAttribute("BlockWorldSelected");
            yield return Tap(_keyboard.digit1Key);
            audit.Append("7 hotbar: key 3 chose ").Append(three).Append(", one wheel notch then chose ").Append(wheel).Append(" | ");

            // 8. Survival counts: break one block.
            yield return Tap(_keyboard.gKey);
            object creative = Workspace().GetAttribute("BlockWorldCreative");
            (int cx, int cz, int cground) = CellAhead(2);
            RbxVector3 countFace = new((cx + 0.5f) * Cell, (cground + 1) * Cell, (cz + 0.5f) * Cell);
            yield return Aim(Eye(), countFace);
            object targetCell = Workspace().GetAttribute("CrosshairCell");
            RbxVector3 aimed = targetCell is RbxVector3 cell ? cell : new RbxVector3(cx, cground, cz);
            string name = TerrainMaterials.GetName(Terrain().Store.GetMaterial((int)aimed.X, (int)aimed.Y, (int)aimed.Z));
            double countBefore = Number(Workspace().GetAttribute("BlockWorldCount_" + name));
            yield return Click(_mouse.leftButton);
            double countAfterBreak = Number(Workspace().GetAttribute("BlockWorldCount_" + name));
            yield return Tap(_keyboard.gKey);
            audit.Append("8 survival (creative ").Append(creative).Append("): breaking ").Append(name).Append(" at ")
                .Append(targetCell).Append(" took its count from ").Append(countBefore).Append(" to ").Append(countAfterBreak)
                .Append(" | ");

            // 9. A villager in your way.
            RbxInstance mira = Workspace().FindFirstChild("Mira")?.FindFirstChild("HumanoidRootPart");
            if (mira != null)
            {
                RbxVector3 miraAt = CurrentWorld().PartSink.GetLivePositionStuds(mira.Id);
                RbxVector3 toward = new RbxVector3(miraAt.X - home.X, 0f, miraAt.Z - home.Z).Unit;
                PutPlayer(miraAt - toward * 8f + new RbxVector3(0f, 1f, 0f));
                yield return Frames(30);
                yield return Aim(Eye(), new RbxVector3(miraAt.X, Eye().Y, miraAt.Z));
                float closest = float.MaxValue;
                Press(_keyboard.wKey);
                for (int frame = 0; frame < 90; frame++)
                {
                    yield return null;
                    RbxVector3 me = RootPosition(Humanoid());
                    RbxVector3 her = CurrentWorld().PartSink.GetLivePositionStuds(mira.Id);
                    closest = Mathf.Min(closest, new RbxVector3(me.X - her.X, 0f, me.Z - her.Z).Magnitude);
                }

                Release(_keyboard.wKey);
                RbxVector3 meNow = RootPosition(Humanoid());
                yield return Aim(ClearView(meNow, meNow + side * 8f + new RbxVector3(0f, 3f, 0f)), meNow + new RbxVector3(0f, 1f, 0f));
                yield return ShootWithUi("audit_9_villager_in_the_way.png");
                audit.Append("9 walking into Mira: closest root to root ").Append(closest.ToString("0.0")).Append(" studs | ");
            }

            // 10. Digging down one block and climbing out, then two blocks and trying again.
            int digFloor = FeetRow() - 1;
            int dx0 = Mathf.FloorToInt(home.X / Cell - flat.X * 4f);
            int dz0 = Mathf.FloorToInt(home.Z / Cell - flat.Z * 4f);
            LevelPatch(dx0, dz0, digFloor);
            FillBox(dx0 - 1, digFloor - 3, dz0 - 1, dx0 + 1, digFloor - 1, dz0 + 1, "Rock");
            StandOnCell(dx0, digFloor, dz0);
            yield return Frames(40);
            System.Text.StringBuilder digging = new();
            for (int depth = 1; depth <= 2; depth++)
            {
                float topY = RootY();
                yield return Aim(Eye(), Eye() - new RbxVector3(0f, 8f, 0f));
                yield return Click(_mouse.leftButton);
                yield return Frames(40);
                float bottomY = RootY();
                yield return Aim(Eye(), Eye() + flat * 10f);
                Press(_keyboard.wKey);
                Press(_keyboard.spaceKey);
                yield return Frames(90);
                Release(_keyboard.spaceKey);
                Release(_keyboard.wKey);
                yield return Frames(30);
                float outY = RootY();
                digging.Append(depth).Append(" deep (sank ").Append(((topY - bottomY) / Cell).ToString("0.0"))
                    .Append(" blocks): walking and jumping climbed ").Append(((outY - bottomY) / Cell).ToString("0.0")).Append(" blocks; ");
                StandOnCell(dx0, digFloor - depth, dz0);
                yield return Frames(30);
            }

            audit.Append("10 digging down: ").Append(digging).Append("| ");

            // 11. A pillar under yourself: look down, jump, and place under your feet at the top of the jump, three times.
            int pillarFloor = FeetRow() - 1;
            int ux = Mathf.FloorToInt(home.X / Cell + flat.X * 4f + side.X * 4f);
            int uz = Mathf.FloorToInt(home.Z / Cell + flat.Z * 4f + side.Z * 4f);
            LevelPatch(ux, uz, pillarFloor);
            FillBox(ux, pillarFloor + 1, uz, ux, pillarFloor + 8, uz, "Air");
            StandOnCell(ux, pillarFloor, uz);
            yield return Frames(40);
            float pillarStart = RootY();
            for (int block = 0; block < 3; block++)
            {
                // WHY aim before the jump: Aim waits for the camera's pose, and by then the jump would be over.
                yield return Aim(Eye(), Eye() - new RbxVector3(0f, 8f, 0f));
                float footing = RootY();
                Press(_keyboard.spaceKey);
                for (int frame = 0; frame < 40 && RootY() < footing + Cell + 0.2f; frame++)
                {
                    yield return null;
                }

                Release(_keyboard.spaceKey);
                yield return Tap(_mouse.rightButton);
                yield return Frames(40);
            }

            float pillarTop = RootY();
            RbxVector3 pillarAt = RootPosition(Humanoid());
            yield return Aim(ClearView(pillarAt, pillarAt + new RbxVector3(0f, 2f, 0f) - flat * 12f + side * 6f), pillarAt - new RbxVector3(0f, 4f, 0f));
            yield return ShootWithUi("audit_11_pillar.png");
            audit.Append("11 jump and place under you, three times: rose ").Append(((pillarTop - pillarStart) / Cell).ToString("0.0"))
                .Append(" blocks");

            // 12. The in-world chat with no model set up: T, then a small build request where you look.
            PutPlayer(home + new RbxVector3(0f, 1f, 0f));
            yield return Frames(30);
            (int ax, int az, int aground) = CellAhead(3);
            yield return Aim(Eye(), new RbxVector3((ax + 0.5f) * Cell, (aground + 1) * Cell, (az + 0.5f) * Cell));
            CoreAI.ChatOverlay.ChatOverlayController overlay = Overlay();
            yield return Tap(_keyboard.tKey);
            yield return Frames(5);
            bool opened = overlay.IsInputVisible;
            int chatStart = overlay.Feed.Lines.Count;
            string revisionBefore = TerrainHash(Terrain());
            overlay.Submit("build a small stone tower here");
            yield return WaitForLine(overlay, chatStart, line => line.Kind != CoreAI.ChatOverlay.ChatFeedLineKind.Player, 900);
            yield return Frames(30);
            yield return ShootWithUi("audit_12_chat_no_model.png");
            bool changed = TerrainHash(Terrain()) != revisionBefore;
            audit.Append(" | 12 chat with no model: T opened the chat ").Append(opened).Append("; the chat then read '")
                .Append(Lines(overlay, chatStart)).Append("'; the world changed ").Append(changed);

            Record("playtest audit: " + audit);
            Report();
        }

        // ---- a live model builds where the player looks (off unless COREAI_BLOCKWORLD_LIVE_URL names an endpoint) ------

        /// <summary>
        /// The model the live endpoint serves: COREAI_BLOCKWORLD_LIVE_MODEL, else the first id its /models lists.
        /// </summary>
        private static string LiveModelId(string url)
        {
            string named = System.Environment.GetEnvironmentVariable("COREAI_BLOCKWORLD_LIVE_MODEL");
            if (!string.IsNullOrEmpty(named))
            {
                return named;
            }

            using System.Net.Http.HttpClient http = new() { Timeout = System.TimeSpan.FromSeconds(10) };
            string list = http.GetStringAsync(url.TrimEnd('/') + "/models").GetAwaiter().GetResult();
            System.Text.RegularExpressions.Match id = System.Text.RegularExpressions.Regex.Match(list, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            return id.Success ? id.Groups[1].Value : "";
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator BlockWorldScene_ALiveModel_BuildsASmallTowerWhereThePlayerLooks()
        {
            string url = System.Environment.GetEnvironmentVariable("COREAI_BLOCKWORLD_LIVE_URL");
            if (string.IsNullOrEmpty(url))
            {
                Assert.Ignore("set COREAI_BLOCKWORLD_LIVE_URL to an OpenAI-compatible endpoint to run the live-model build");
            }

            string modelId = LiveModelId(url);
            CoreAI.Infrastructure.Llm.OpenAiHttpOptions options = new()
            {
                UseOpenAiCompatibleHttp = true,
                ApiBaseUrl = url,
                ApiKey = "",
                Model = modelId,
                RequestTimeoutSeconds = 170
            };
            CoreAI.Ai.ILlmClient live = new CoreAI.Infrastructure.Llm.OpenAiChatLlmClient(options,
                CoreAI.Infrastructure.Llm.CoreAISettingsAsset.Instance, CoreAI.Infrastructure.Logging.GameLoggerUnscopedFallback.Instance,
                supportsNativeToolCalling: true);
            yield return OpenScene(live);
            yield return WaitUntilStanding();
            yield return SetClock(12d);

            // Fresh, level land three blocks ahead, the crosshair on its top face.
            (int x, int z, int ground) = CellAhead(3);
            FillBox(x - 3, ground, z - 3, x + 3, ground, z + 3, "Grass");
            FillBox(x - 3, ground + 1, z - 3, x + 3, ground + 8, z + 3, "Air");
            yield return Frames(10);
            RbxVector3 face = new((x + 0.5f) * Cell, (ground + 1) * Cell, (z + 0.5f) * Cell);
            yield return Aim(Eye(), face);
            object place = Workspace().GetAttribute("CrosshairPlaceCell");
            RbxVector3 baseCell = place is RbxVector3 cell ? cell : new RbxVector3(x, ground + 1, z);
            int bx = (int)baseCell.X;
            int by = (int)baseCell.Y;
            int bz = (int)baseCell.Z;
            Dictionary<long, (byte, byte)> before = ReadBox(Terrain(), bx - 6, by - 3, bz - 6, bx + 6, by + 13, bz + 6);

            CoreAI.ChatOverlay.ChatOverlayController overlay = Overlay();
            yield return Tap(_keyboard.tKey);
            int start = overlay.Feed.Lines.Count;
            float asked = Time.realtimeSinceStartup;
            overlay.Submit("build a small stone tower here, 3 blocks wide and 5 high");
            while (Time.realtimeSinceStartup - asked < 180f)
            {
                yield return null;
                bool answered = false;
                for (int index = start; index < overlay.Feed.Lines.Count; index++)
                {
                    CoreAI.ChatOverlay.ChatFeedLine line = overlay.Feed.Lines[index];
                    answered |= line.Kind != CoreAI.ChatOverlay.ChatFeedLineKind.Player && !line.IsStreaming && line.Text.Length > 0;
                }

                if (answered && !overlay.IsTurnActive)
                {
                    break;
                }
            }

            float seconds = Time.realtimeSinceStartup - asked;
            yield return Frames(30);
            Dictionary<long, (byte, byte)> after = ReadBox(Terrain(), bx - 6, by - 3, bz - 6, bx + 6, by + 13, bz + 6);
            int changed = 0;
            Dictionary<string, int> materials = new();
            foreach (KeyValuePair<long, (byte, byte)> entry in before)
            {
                if (after[entry.Key] != entry.Value)
                {
                    changed++;
                    string name = TerrainMaterials.GetName(after[entry.Key].Item1);
                    materials[name] = materials.TryGetValue(name, out int count) ? count + 1 : 1;
                }
            }

            System.Text.StringBuilder feed = new();
            for (int index = start; index < overlay.Feed.Lines.Count; index++)
            {
                CoreAI.ChatOverlay.ChatFeedLine line = overlay.Feed.Lines[index];
                feed.Append(line.Kind).Append(' ').Append(line.Speaker).Append(": ").Append(line.Text).Append(" | ");
            }

            yield return Aim(ClearView(face, face + new RbxVector3(-14f, 10f, -14f)), face + new RbxVector3(0f, 8f, 0f));
            yield return ShootWithUi("live_model_tower.png");
            Record("live model " + modelId + " at " + url + ": " + seconds.ToString("0.0") + " s; " + changed
                   + " cells changed within 6 of " + baseCell + " (" + string.Join(", ", materials.Select(pair => pair.Key + " " + pair.Value))
                   + "); the chat: " + feed);
            Expect(changed > 0, "the live model changed the land where the player looked");
            Report();
        }

        // ---- the body: 1.8 blocks, steps, a jump, a sprint, flight, a villager, a crown overhead ----------------------

        private float HeadTopY()
        {
            RunOnce("test_head_top_", "local c = game:GetService('Players'):GetPlayers()[1].Character local high = -math.huge "
                                      + "for _, p in ipairs(c:GetChildren()) do if p:IsA('BasePart') then "
                                      + "high = math.max(high, p.Position.Y + p.Size.Y / 2) end end "
                                      + "workspace:SetAttribute('TestHeadTop', high)");
            return (float)System.Convert.ToDouble(Workspace().GetAttribute("TestHeadTop") ?? 0d);
        }

        private string State()
        {
            return Humanoid().GetState() + (Humanoid().IsFlying ? " (flying)" : "");
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator BlockWorldScene_TheBody_StepsSprintFlightAndAVillager()
        {
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            yield return SetClock(12d);
            System.Text.StringBuilder body = new();
            RbxVector3 look = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawnLook");
            RbxVector3 flat = new RbxVector3(look.X, 0f, look.Z).Unit;
            RbxVector3 side = new RbxVector3(flat.Z, 0f, -flat.X);

            // A cleared, level yard ahead: a one-block and a two-block step side by side.
            (int x, int z, int ground) = CellAhead(6);
            FillBox(x - 5, ground, z - 5, x + 5, ground, z + 5, "Grass");
            FillBox(x - 5, ground + 1, z - 5, x + 5, ground + 8, z + 5, "Air");
            int sx = Mathf.RoundToInt(flat.X);
            int sz = Mathf.RoundToInt(flat.Z);
            int px = Mathf.RoundToInt(side.X);
            int pz = Mathf.RoundToInt(side.Z);
            int startX = x - sx * 3;
            int startZ = z - sz * 3;
            FillCells(startX + sx * 2, startZ + sz * 2, ground + 1, ground + 1, "Rock");
            FillCells(startX + sx * 2 + px * 2, startZ + sz * 2 + pz * 2, ground + 1, ground + 2, "Rock");
            yield return Tap(_keyboard.vKey);
            StandOnCell(startX, ground, startZ);
            yield return Frames(40);
            float feet = FeetY();
            float top = HeadTopY();
            RbxVector3 me = RootPosition(Humanoid());
            RbxVector3 sideView = me + flat * 8f - side * 16f + new RbxVector3(0f, 5f, 0f);
            yield return Aim(sideView, me + flat * 6f + side * 3f);
            yield return ShootWithUi("body_steps.png");
            body.Append("height ").Append((top - feet).ToString("0.00")).Append(" studs (")
                .Append(((top - feet) / Cell).ToString("0.00")).Append(" blocks), eyes ")
                .Append((EyePosition().Y - feet).ToString("0.00")).Append(" over the feet | ");

            // Onto the one-block step: walk at it and jump, seen from the side, three frames.
            float startY = RootY();
            RunOnce("test_walk_step_", "local h = game:GetService('Players'):GetPlayers()[1].Character.Humanoid "
                                       + "h:Move(Vector3.new(" + Lua(flat.X) + ", 0, " + Lua(flat.Z) + "), false) h.Jump = true");
            float jumpStart = Time.time;
            int jumpShot = 1;
            float highest = startY;
            while (Time.time - jumpStart < 1.2f)
            {
                yield return null;
                highest = Mathf.Max(highest, RootY());
                if (jumpShot <= 3 && Time.time - jumpStart >= new[] { 0.05f, 0.3f, 1.1f }[jumpShot - 1])
                {
                    yield return ShootWithUi("body_jump_" + jumpShot + ".png");
                    jumpShot++;
                }
            }

            RunOnce("test_stop_", "game:GetService('Players'):GetPlayers()[1].Character.Humanoid:Move(Vector3.zero, false)");
            yield return Frames(20);
            float onStep = RootY() - startY;
            body.Append("walking at the one-block step with a jump: rose ").Append((highest - startY).ToString("0.0"))
                .Append(", ended ").Append(onStep.ToString("0.0")).Append(" studs up (on it: ").Append(onStep > 3f).Append(") | ");

            // Sprint: W alone, then Left Control and W, each 0.6 s from the same place.
            StandOnCell(startX - sx * 1, ground, startZ - sz * 1 - pz * 3 + pz * 3);
            yield return Frames(30);
            yield return Aim(Eye() - flat * 6f + new RbxVector3(0f, 3f, 0f), Eye() + flat * 20f);
            RbxVector3 walkFrom = RootPosition(Humanoid());
            yield return Hold(_keyboard.wKey, 0.6f);
            float walked = (RootPosition(Humanoid()) - walkFrom).Magnitude;
            StandOnCell(startX - sx * 1, ground, startZ - sz * 1);
            yield return Frames(30);
            yield return Aim(Eye() - flat * 6f + new RbxVector3(0f, 3f, 0f), Eye() + flat * 20f);
            RbxVector3 sprintFrom = RootPosition(Humanoid());
            Press(_keyboard.leftCtrlKey);
            Press(_keyboard.wKey);
            float sprintStart = Time.time;
            bool shot = false;
            while (Time.time - sprintStart < 0.6f)
            {
                yield return null;
                if (!shot && Time.time - sprintStart > 0.4f)
                {
                    shot = true;
                    yield return ShootWithUi("body_sprint.png");
                }
            }

            object sprintSpeed = Humanoid().WalkSpeed;
            Release(_keyboard.wKey);
            Release(_keyboard.leftCtrlKey);
            yield return Frames(5);
            float sprinted = (RootPosition(Humanoid()) - sprintFrom).Magnitude;
            body.Append("in 0.6 s walking covered ").Append(walked.ToString("0.0")).Append(" studs, sprinting ")
                .Append(sprinted.ToString("0.0")).Append(" (WalkSpeed while sprinting ").Append(sprintSpeed).Append(") | ");

            // Flight: Space twice, from a hover Space and Left Shift 0.5 s each, then Left Control and W in the air.
            StandOnCell(startX, ground, startZ);
            yield return Frames(30);
            yield return Aim(Eye() - flat * 6f + new RbxVector3(0f, 3f, 0f), Eye() + flat * 20f);
            Press(_keyboard.spaceKey);
            yield return Frames(3);
            Release(_keyboard.spaceKey);
            yield return Frames(3);
            Press(_keyboard.spaceKey);
            yield return Frames(3);
            Release(_keyboard.spaceKey);
            yield return Frames(30);
            string flightState = State();
            float hoverA = RootY();
            yield return Hold(_keyboard.spaceKey, 0.5f);
            yield return Frames(30);
            float hoverB = RootY();
            yield return Hold(_keyboard.leftShiftKey, 0.5f);
            yield return Frames(30);
            float hoverC = RootY();
            RbxVector3 flyFrom = RootPosition(Humanoid());
            yield return Hold(_keyboard.wKey, 0.4f);
            float flown = (new RbxVector3(RootPosition(Humanoid()).X - flyFrom.X, 0f, RootPosition(Humanoid()).Z - flyFrom.Z)).Magnitude;
            yield return Frames(30);
            RbxVector3 fastFrom = RootPosition(Humanoid());
            Press(_keyboard.leftCtrlKey);
            yield return Hold(_keyboard.wKey, 0.4f);
            Release(_keyboard.leftCtrlKey);
            float flownFast = (new RbxVector3(RootPosition(Humanoid()).X - fastFrom.X, 0f, RootPosition(Humanoid()).Z - fastFrom.Z)).Magnitude;
            body.Append("flight: after Space twice ").Append(flightState).Append("; from a hover Space 0.5 s rose ")
                .Append((hoverB - hoverA).ToString("0.0")).Append(", Left Shift 0.5 s sank ").Append((hoverB - hoverC).ToString("0.0"))
                .Append("; W 0.4 s flew ").Append(flown.ToString("0.0")).Append(", Left Control and W 0.4 s ")
                .Append(flownFast.ToString("0.0")).Append(" | ");
            Press(_keyboard.spaceKey);
            yield return Frames(3);
            Release(_keyboard.spaceKey);
            yield return Frames(3);
            Press(_keyboard.spaceKey);
            yield return Frames(3);
            Release(_keyboard.spaceKey);
            yield return Frames(90);

            // A villager beside the player, seen from the side.
            RbxInstance mira = Workspace().FindFirstChild("Mira")?.FindFirstChild("HumanoidRootPart");
            if (mira != null)
            {
                RbxVector3 miraAt = CurrentWorld().PartSink.GetLivePositionStuds(mira.Id);
                PutPlayer(miraAt + side * 3f + new RbxVector3(0f, 2f, 0f));
                yield return Frames(60);
                RbxVector3 pair = (RootPosition(Humanoid()) + CurrentWorld().PartSink.GetLivePositionStuds(mira.Id)) / 2f;
                yield return Aim(ClearView(pair, pair + flat * 14f + new RbxVector3(0f, 3f, 0f)), pair + new RbxVector3(0f, 1f, 0f));
                yield return ShootWithUi("body_villager.png");
                RunOnce("test_mira_height_", "local m = workspace.Mira local low, high = math.huge, -math.huge "
                                             + "for _, p in ipairs(m:GetChildren()) do if p:IsA('BasePart') then "
                                             + "low = math.min(low, p.Position.Y - p.Size.Y / 2) high = math.max(high, p.Position.Y + p.Size.Y / 2) end end "
                                             + "workspace:SetAttribute('TestMiraHeight', high - low)");
                body.Append("Mira ").Append(System.Convert.ToDouble(Workspace().GetAttribute("TestMiraHeight") ?? 0d).ToString("0.00"))
                    .Append(" studs tall beside the player | ");
            }

            // Under the crown of a four-block trunk, in first person: the eyes are not in the leaves.
            StandOnCell(startX + px * 3, ground, startZ + pz * 3);
            yield return Frames(30);
            int tx = startX + px * 3 + sx;
            int tz = startZ + pz * 3 + sz;
            FillCells(tx, tz, ground + 1, ground + 4, "Mud");
            FillBox(tx - 2, ground + 4, tz - 2, tx + 2, ground + 5, tz + 2, "LeafyGrass");
            FillCells(tx, tz, ground + 4, ground + 4, "Mud");
            yield return Tap(_keyboard.vKey);
            yield return Frames(30);
            RunOnce("test_eye_cell_", "local p = workspace.CurrentCamera.CFrame.Position local t = workspace.Terrain "
                                      + "local c = t:WorldToCell(p) local corner = t:CellCornerToWorld(c.X, c.Y, c.Z) "
                                      + "local m = t:ReadVoxels(Region3.new(corner, corner + Vector3.new(4, 4, 4)), 4) "
                                      + "workspace:SetAttribute('TestEyeMaterial', m[1][1][1].Name) workspace:SetAttribute('TestEyeY', p.Y)");
            yield return ShootWithUi("body_under_crown.png");
            body.Append("first person under a crown on a four-block trunk: the eye is in ").Append(Workspace().GetAttribute("TestEyeMaterial"))
                .Append(", ").Append((System.Convert.ToDouble(Workspace().GetAttribute("TestEyeY") ?? 0d) - (ground + 1) * Cell).ToString("0.0"))
                .Append(" studs over the floor, the crown from ").Append(4 * Cell).Append(" studs");

            Record("the body: " + body);
            Expect(top - feet > 6.8f && top - feet < 7.6f, "the player is 1.8 blocks tall (7.2 studs)");
            Expect(onStep > 3f, "walking at a one-block step with a jump ends on top of it");
            Expect(sprinted > walked * 1.15f, "a sprint covers more ground than a walk");
            Expect(!Equals(Workspace().GetAttribute("TestEyeMaterial"), "LeafyGrass"), "standing under a crown, the eyes are not in the leaves");
            Report();
        }
    }
}
#endif
