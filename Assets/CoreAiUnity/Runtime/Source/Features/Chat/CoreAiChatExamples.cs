using System.Collections.Generic;

namespace CoreAI.Chat
{
    /// <summary>
    /// A single declarative chat example: a titled, ready-to-run prompt the player can insert into the chat
    /// input with one click (it is NOT auto-sent). Pure data so the list is trivially extendable.
    /// </summary>
    public readonly struct CoreAiChatExample
    {
        /// <summary>Stable identifier.</summary>
        public string Id { get; }

        /// <summary>Short menu label.</summary>
        public string Title { get; }

        /// <summary>Full prompt text inserted into the chat input.</summary>
        public string Message { get; }

        public CoreAiChatExample(string id, string title, string message)
        {
            Id = id;
            Title = title;
            Message = message;
        }
    }

    /// <summary>
    /// Built-in example prompts surfaced by the chat panel's Examples menu. Internal static list; games can
    /// grow it later. Each entry stands on its own — the player picks one, reviews it, and presses send.
    /// </summary>
    public static class CoreAiChatExamples
    {
        // WHY: Lua examples use ONLY APIs the Programmer prompt documents (hooks_every / hooks_on('tick'),
        // input_key / input_key_down, coreai_world_spawn / _set_color / _change / _destroy with primitive
        // prefab keys, store_set / store_get). The arena example compiles but fails at runtime on purpose,
        // to demo the read-error → fix → reload loop.

        private const string TetrisMessage =
            "Create a mod named tetris with this code and load it:\n" +
            "```lua\n" + TetrisLua + "```";

        private const string ClickerMessage =
            "Create a mod named clicker with this code and load it:\n" +
            "```lua\n" + ClickerLua + "```";

        private const string CastleMessage =
            "Build the most impressive castle you can within the -9..9 build volume (one Unity unit = one " +
            "meter, y is height, ground at y=0). Use the world_command tool: action='spawn', a DISTINCT " +
            "targetName per object, and prefabKey — one of cube, sphere, cylinder, capsule. Note cylinder " +
            "and capsule are already 2m tall unscaled at 1m diameter, so for a tower/pillar of height H use " +
            "scaleY = H/2 and place its pivot at y = H/2. Use scaleX/scaleY/scaleZ for walls, floors and " +
            "towers and rotations fx/fy/fz for angled parts. Make it clearly read as a castle but compose it " +
            "your way: a lived-in courtyard (a well, market stalls, crates, barrels, a campfire, benches), " +
            "buildings of varied heights and rooflines, a gatehouse with a road leading up to it, and a " +
            "surrounding world past the walls — outbuildings, tents, trees, a pond or moat, rocks, fences. " +
            "Color each major group with action='set_color' and an HTML color (grey stone '#9aa0a8' walls " +
            "and towers, dark-red '#8e3b2f' roofs, brown '#6b4a2f' gate and bridge, green '#3f7d3a' " +
            "treetops, blue '#3b6ea5' moat water) — an all-grey castle reads as unfinished. Vary positions, " +
            "sizes and angles, and keep spawning until the scene feels full.";

        private const string ArenaMessage =
            "Load this mod named arena_spawner, then find out why it fails and fix it:\n" +
            "```lua\n" + ArenaLua + "```";

        private static readonly CoreAiChatExample[] Examples =
        {
            new("tetris", "Tetris mod", TetrisMessage),
            new("clicker", "Clicker game", ClickerMessage),
            new("castle", "Build a castle", CastleMessage),
            new("arena", "Fix the broken arena", ArenaMessage),
        };

        /// <summary>All built-in example prompts, in display order.</summary>
        public static IReadOnlyList<CoreAiChatExample> All => Examples;

        // A compact but genuinely playable falling-blocks game: 10x18 grid rendered from cubes, arrow keys to
        // move/rotate, blocks fall and lock, full rows clear, top-out ends the game and 'r' restarts.
        private const string TetrisLua =
@"local COLS, ROWS = 10, 18
local CELL = 1.0
local ORIGIN_X, ORIGIN_Y = -4.5, 8.5

local board = {}
local piece = nil
local fallTimer = 0
local fallEvery = 0.5
local gameOver = false

local SHAPES = {
  { color = '#33ccff', cells = { {0,0},{1,0},{2,0},{3,0} } },
  { color = '#ffcc00', cells = { {0,0},{1,0},{0,1},{1,1} } },
  { color = '#cc66ff', cells = { {0,0},{1,0},{2,0},{1,1} } },
  { color = '#66ff66', cells = { {1,0},{2,0},{0,1},{1,1} } },
  { color = '#ff6666', cells = { {0,0},{1,0},{1,1},{2,1} } },
  { color = '#6699ff', cells = { {0,0},{0,1},{1,1},{2,1} } },
  { color = '#ff9933', cells = { {2,0},{0,1},{1,1},{2,1} } },
}

local function reset_board()
  board = {}
  for y = 1, ROWS do
    board[y] = {}
    for x = 1, COLS do board[y][x] = nil end
  end
end

local function cell_name(x, y)
  return 'tetris_cell_' .. x .. '_' .. y
end

local function spawn_grid()
  for y = 1, ROWS do
    for x = 1, COLS do
      coreai_world_spawn({
        prefab = 'cube',
        name = cell_name(x, y),
        x = ORIGIN_X + (x - 1) * CELL,
        y = ORIGIN_Y - (y - 1) * CELL,
        z = 0,
        scaleX = 0.92, scaleY = 0.92, scaleZ = 0.92,
      })
      coreai_world_set_color(cell_name(x, y), '#101820')
    end
  end
end

local function new_piece()
  local def = SHAPES[math.random(1, #SHAPES)]
  local cells = {}
  for i = 1, #def.cells do
    cells[i] = { def.cells[i][1], def.cells[i][2] }
  end
  return { cells = cells, x = 4, y = 1, color = def.color }
end

local function collides(p, ox, oy, cells)
  cells = cells or p.cells
  for i = 1, #cells do
    local cx = p.x + cells[i][1] + ox
    local cy = p.y + cells[i][2] + oy
    if cx < 1 or cx > COLS or cy > ROWS then return true end
    if cy >= 1 and board[cy][cx] then return true end
  end
  return false
end

local function lock_piece(p)
  for i = 1, #p.cells do
    local cx = p.x + p.cells[i][1]
    local cy = p.y + p.cells[i][2]
    if cy >= 1 then board[cy][cx] = p.color end
  end
end

local function clear_lines()
  local y = ROWS
  while y >= 1 do
    local full = true
    for x = 1, COLS do
      if not board[y][x] then full = false break end
    end
    if full then
      for yy = y, 2, -1 do
        for x = 1, COLS do board[yy][x] = board[yy - 1][x] end
      end
      for x = 1, COLS do board[1][x] = nil end
    else
      y = y - 1
    end
  end
end

local function rotate(p)
  local cells = {}
  for i = 1, #p.cells do
    local x, y = p.cells[i][1], p.cells[i][2]
    cells[i] = { -y, x }
  end
  if not collides(p, 0, 0, cells) then p.cells = cells end
end

local function render()
  for y = 1, ROWS do
    for x = 1, COLS do
      coreai_world_set_color(cell_name(x, y), board[y][x] or '#101820')
    end
  end
  if piece then
    for i = 1, #piece.cells do
      local cx = piece.x + piece.cells[i][1]
      local cy = piece.y + piece.cells[i][2]
      if cy >= 1 and cy <= ROWS and cx >= 1 and cx <= COLS then
        coreai_world_set_color(cell_name(cx, cy), piece.color)
      end
    end
  end
end

local function start_game()
  reset_board()
  gameOver = false
  piece = new_piece()
  fallTimer = 0
  store_set('tetris_started', '1')
end

-- Frame the board: drop the Main Camera straight in front of the grid so the game is always on
-- screen no matter where the host scene left the camera.
if coreai_world_exists('Main Camera') then
  coreai_world_change('Main Camera', { x = 0, y = 0, z = -22, rx = 0, ry = 0, rz = 0 })
end

spawn_grid()
start_game()

hooks_on('tick', function()
  if gameOver then
    if input_key_down('r') then start_game() end
    return
  end

  if input_key_down('left') and not collides(piece, -1, 0) then piece.x = piece.x - 1 end
  if input_key_down('right') and not collides(piece, 1, 0) then piece.x = piece.x + 1 end
  if input_key_down('up') then rotate(piece) end
  if input_key('down') then fallEvery = 0.05 else fallEvery = 0.5 end

  fallTimer = fallTimer + 0.016
  if fallTimer >= fallEvery then
    fallTimer = 0
    if not collides(piece, 0, 1) then
      piece.y = piece.y + 1
    else
      lock_piece(piece)
      clear_lines()
      piece = new_piece()
      if collides(piece, 0, 0) then gameOver = true end
    end
  end

  render()
end)
";

        // A tiny idle/clicker game: left-click the golden cube to earn points, every 10 points stacks a
        // gold coin into a tower, a passive +1/0.5s keeps it alive unattended, and 'r' resets. Uses only
        // documented globals (input_mouse_button / input_key_down, coreai_world_*, store_*, report).
        private const string ClickerLua =
@"local BTN = 'clicker_button'
local MAX_COINS = 24
local score = tonumber(store_get('clicker_score')) or 0
local coins = 0
local pulse = 0
local was_down = false

-- Frame the scene on the button so the game is always visible in the host scene.
if coreai_world_exists('Main Camera') then
  coreai_world_change('Main Camera', { x = 0, y = 2, z = -12, rx = 6, ry = 0, rz = 0 })
end

coreai_world_spawn({ prefab = 'cube', name = BTN, x = 0, y = 2, z = 0, scaleX = 1.5, scaleY = 1.5, scaleZ = 1.5 })
coreai_world_set_color(BTN, '#FFC400')

local function coin_name(i) return 'clicker_coin_' .. i end

local function spawn_coin(i)
  if i > MAX_COINS then return end
  local name = coin_name(i)
  coreai_world_spawn({
    prefab = 'cube', name = name,
    x = -4.0, y = 0.4 + (i - 1) * 0.45, z = 0,
    scaleX = 0.8, scaleY = 0.35, scaleZ = 0.8,
  })
  coreai_world_set_color(name, '#FFD700')
end

local function publish()
  store_set('clicker_score', tostring(score))
  report(string.format('SCORE=%d coins=%d', score, coins))
end

local function add_points(n)
  score = score + n
  local want = math.min(math.floor(score / 10), MAX_COINS)
  while coins < want do
    coins = coins + 1
    spawn_coin(coins)
  end
  if n > 0 then pulse = 6 end
  publish()
end

-- Restore the coin tower from the persisted score on load.
add_points(0)

hooks_on('tick', function()
  local down = input_mouse_button(0)
  if down and not was_down then add_points(1) end
  was_down = down
  if input_key_down('r') then
    for i = 1, coins do coreai_world_destroy(coin_name(i)) end
    coins = 0
    score = 0
    publish()
  end
end)

-- Passive income so the tower visibly grows even without a player (idle-game style).
hooks_every(0.5, function() add_points(1) end)

-- Click feedback: the button pops on each earn and eases back to rest size.
hooks_every(0.05, function()
  if pulse > 0 then pulse = pulse - 1 end
  local s = 1.5 + pulse * 0.07
  coreai_world_change(BTN, { scaleX = s, scaleY = s, scaleZ = s })
end)
";

        // Compiles cleanly, crashes at runtime on purpose (repair-loop demo):
        //  1) reads cfg.spawn_count, but the field is spawnCount -> arithmetic on a nil value.
        //  2) hooks_every(0, ...) is below the 0.05s minimum interval.
        private const string ArenaLua =
@"local cfg = {
  spawnCount = 5,
  radius = 6.0,
  interval = 0.25,
}

local wave = 0

local function spawn_wave()
  wave = wave + 1
  -- BUG 1: cfg.spawn_count is nil (the field is spawnCount) -> arithmetic on a nil value.
  local count = cfg.spawn_count * wave
  for i = 1, count do
    local angle = (i / count) * math.pi * 2
    coreai_world_spawn({
      prefab = 'capsule',
      name = 'arena_enemy_' .. wave .. '_' .. i,
      x = math.cos(angle) * cfg.radius,
      y = 1.0,
      z = math.sin(angle) * cfg.radius,
    })
  end
end

-- BUG 2: 0 is below the 0.05s minimum timer interval; use cfg.interval instead.
hooks_every(0, spawn_wave)
";
    }
}
