//reference System.dll
//reference System.Core.dll
//reference System.Xml.dll

// Thanks goes out to D_Flat for suggesting the way to write the command, in a way I think is absolutely perfect

using MCGalaxy.Events.LevelEvents;
using MCGalaxy.Maths;
using MCGalaxy.Tasks;
using MCGalaxy.Events.PlayerEvents;
using System;
using System.Collections.Generic;
using System.IO;
using BlockID = System.UInt16;
using MCGalaxy.Network;
using System.Linq;
using System.Xml.Serialization;
using MCGalaxy.Blocks;
using MCGalaxy.Commands;

namespace MCGalaxy
{
    /***********************
    * ANIMATION DATA TYPES *
    ************************/
    // Contains all the animation blocks for a given map, as well as the task schedulers (they aren't in animation handler because they tick at a per-level rate)
    public sealed class MapAnimation
    {
        public BufferedBlockSender buffer;
        static readonly ushort DEFAULT_SPEED = 100;
        public bool bRunning;
        public bool bEditingLock;   // Stops all updates writing/deleting to/from the block list
        public ushort _currentTick;
        public List<AnimBlock> _blocks;
        public ushort _numLoops;
        public ushort _speed;
        public bool bKiller;

        private SchedulerTask _task;
        private readonly Level _level;
        private static Scheduler instance;

        private readonly object activateLock = new object();
        private readonly object deactivateLock = new object();

        public MapAnimation(bool running, ushort currentTick, ushort numLoops, Level level, ushort speed, bool killer)
        {
            bRunning = running; bEditingLock = true; _currentTick = currentTick; _numLoops = numLoops; _blocks = new List<AnimBlock>(); _level = level; _speed = speed; bKiller = killer;
        }

        public void Activate()
        {
            lock (activateLock)
            {
                //Lazy initialisation of the singular instance of our task Scheduler
                if (instance == null) instance = new Scheduler(String.Format("AnimationsScheduler+{0}", _level.name));

                if (_task != null) return; //Prevent unintentional multiple activations of the same level
                buffer = new BufferedBlockSender(_level);
                _task = instance.QueueRepeat(AnimationHandler.Update, _level, TimeSpan.FromMilliseconds(DEFAULT_SPEED));
            }
        }
        public void Deactivate()
        {
            lock (deactivateLock)
            {
                //Prevent unintentional multiple deactivations of the same level
                if (_task != null)
                {
                    instance.Cancel(_task);
                    _task = null;
                }
            }
            buffer = new BufferedBlockSender();
            _blocks = new List<AnimBlock>();
        }

        public void SetSpeed(ushort speed)
        {
            _speed = speed;
            _task.Delay = TimeSpan.FromMilliseconds(speed);
        }
    }

    // An animated block at a specific coordinate. Contains animation loops
    public sealed class AnimBlock
    {
        public AnimBlock(ushort x, ushort y, ushort z, BlockID block)
        {
            this._x = x; this._y = y; this._z = z; this._currentBlock = block; this._loopList = new SortedList<ushort, AnimLoop>();
        }
        public ushort _x;
        public ushort _y;
        public ushort _z;
        public BlockID _currentBlock;  // Currently visible block
        public SortedList<ushort, AnimLoop> _loopList; // List sorted by index
    }

    // A single animation loop
    public sealed class AnimLoop
    {
        public AnimLoop(ushort interval, ushort duration, short startTick, ushort endTick, BlockID block)
        {
            this._interval = interval; this._duration = duration; this._startTick = startTick; this._endTick = endTick; this._block = block;
        }
        public ushort _interval;         // The period of the loop
        public ushort _duration;          // The length of time we keep seeing the loop
        public short _startTick;      // Animation offset
        public ushort _endTick;        // Animation end
        public BlockID _block;
    }

    /**********
     * PLUGIN *
     **********/

    public class AnimationsPlugin : Plugin
    {
        public override string creator { get { return "Opapinguin"; } }
        public override string MCGalaxy_Version { get { return "1.9.4.0"; } }
        public override string name { get { return "Animations"; } }

        public override void Load(bool startup)
        {
            CreateAnimsDir();

            StartAnimations();

            OnLevelLoadedEvent.Register(HandleLevelLoaded, Priority.Normal);
            OnLevelUnloadEvent.Register(HandleLevelUnload, Priority.Normal);
            OnLevelSaveEvent.Register(HandleLevelSave, Priority.Normal);
            OnLevelRenamedEvent.Register(HandleLevelRenamed, Priority.Normal);
            OnLevelCopiedEvent.Register(HandleLevelCopied, Priority.Normal);
            OnLevelDeletedEvent.Register(HandleLevelDeleted, Priority.Normal);

            OnPlayerClickEvent.Register(HandlePlayerClick, Priority.Normal);

            OnJoinedLevelEvent.Register(HandleJoinedLevel, Priority.Normal);

            InitializeAnimDict();
            AnimationHandler.Activate();

            Command.Register(new CmdAnimation());
        }

        public override void Unload(bool shutdown)
        {
            OnLevelLoadedEvent.Unregister(HandleLevelLoaded);
            OnLevelUnloadEvent.Unregister(HandleLevelUnload);
            OnLevelSaveEvent.Unregister(HandleLevelSave);
            OnLevelRenamedEvent.Unregister(HandleLevelRenamed);
            OnLevelCopiedEvent.Unregister(HandleLevelCopied);
            OnLevelDeletedEvent.Unregister(HandleLevelDeleted);

            OnPlayerClickEvent.Unregister(HandlePlayerClick);

            OnJoinedLevelEvent.Unregister(HandleJoinedLevel);

            AnimationHandler.Deactivate();

            Command.Unregister(Command2.Find("Animation"));
        }

        // Creates the animation directory
        private void CreateAnimsDir()
        {
            if (!Directory.Exists("Animations"))
            {
                try
                {
                    Directory.CreateDirectory("Animations");
                }
                catch (Exception e)
                {
                    Logger.Log(LogType.Error, "Failed to create Animations folder");
                    Logger.Log(LogType.Error, e.StackTrace);
                }
            }
            if (!Directory.Exists("AnimationsBackup"))
            {
                try
                {
                    Directory.CreateDirectory("AnimationsBackup");
                }
                catch (Exception e)
                {
                    Logger.Log(LogType.Error, "Failed to create AnimationsBackup folder");
                    Logger.Log(LogType.Error, e.StackTrace);
                }
            }
        }

        /******************
         * EVENT HANDLERS *
         ******************/

        private void InitializeAnimDict()
        {
            foreach (Level level in LevelInfo.Loaded.Items)
            {
                if (File.Exists(String.Format("Animations/{0}+animation.txt", level.name)))
                {
                    ReadAnimation(level);
                    ReadConfig(level);
                }
            }
        }

        private void HandleLevelDeleted(string level)
        {
            ConditionalDeleteAnimationFile(level);
            ConditionalDeleteAnimationConfigFile(level);
        }

        private void HandleLevelCopied(string srcMap, string dstMap)
        {
            RenameAnimation(srcMap, dstMap);
            RenameAnimationConfig(srcMap, dstMap);
        }

        private void HandleLevelRenamed(string srcMap, string dstMap)
        {
            RenameAnimation(srcMap, dstMap);
            RenameAnimationConfig(srcMap, dstMap);
        }

        private void HandleLevelSave(Level level, ref bool cancel)
        {
            SaveAnimation(level);
            SaveConfig(level);
        }
        private void HandleLevelLoaded(Level level)
        {
            ReadAnimation(level);
            ReadConfig(level);
        }

        private void HandleLevelUnload(Level level, ref bool cancel)
        {
            SaveAnimation(level);
            SaveConfig(level);
            AnimationHandler.RemoveFromActiveLevels(level);
        }

        private void HandlePlayerClick(Player p, MouseButton button, MouseAction action, ushort yaw, ushort pitch, byte entity, ushort x, ushort y, ushort z, TargetBlockFace face)
        {
            if (!p.level.Config.Deletable) return;

            AnimationHandler.SendCurrentFrameBlock(p, x, y, z);
        }

        private void HandleJoinedLevel(Player p, Level prevLevel, Level level, ref bool announce)
        {
            AnimationHandler.SendCurrentFrame(p, level);
        }

        /******************************
        * FILE AND ANIMATION HANDLING *
        *******************************/

        // Starts all animations for maps that have them
        private void StartAnimations()
        {
            foreach (Level level in LevelInfo.Loaded.Items)
            {
                ReadAnimation(level);
                ReadConfig(level);
            }
        }

        // Read the animation file for a level if it exists, then sends it to the level's MapAnimation (creates it if it does not exist)
        public static void ReadAnimation(Level level)
        {
            // File is ordered as <x> <y> <z> <index> <interval> <duration> <start> <end> <blockID>
            List<String> animFile;
            try
            {
                if (File.Exists(String.Format("Animations/{0}+animation.txt", level.name)))
                {
                    string[] logFile = File.ReadAllLines(String.Format("Animations/{0}+animation.txt", level.name));
                    animFile = new List<string>(logFile);
                }
                else
                {
                    return;
                }
            }
            catch (Exception e)
            {
                Logger.Log(LogType.Error, String.Format("Could not read Animations/{0}+animation.txt", level.name));
                Logger.Log(LogType.Error, e.StackTrace);
                return;
            }

            AnimationHandler.ConditionalAddMapAnimation(level);
            AnimationHandler.dictActiveLevels[level.name].bEditingLock = true;

            // Write into the MapAnimation for the level
            foreach (String line in animFile)
            {
                string[] l = line.Split(' ');
                try
                {
                    AnimationHandler.AddLoop(level, Convert.ToUInt16(l[0]), Convert.ToUInt16(l[1]), Convert.ToUInt16(l[2]), Convert.ToUInt16(l[3]), Convert.ToUInt16(l[4]), Convert.ToUInt16(l[5]), Convert.ToInt16(l[6]), Convert.ToUInt16(l[7]), Convert.ToUInt16(l[8]));
                }
                catch (Exception e)
                {
                    Logger.Log(LogType.Error, String.Format("Could not read line \" {0} \" from Animations/{1}+animation.txt", line, level.name));
                    Logger.Log(LogType.Error, e.StackTrace);
                    return;
                }
            }

            AnimationHandler.dictActiveLevels[level.name].bEditingLock = false;
        }

        // Read properties file for animations
        public static void ReadConfig(Level level)
        {
            if (!File.Exists(String.Format("Animations/{0}+animationProps.xml", level.name))) { return; }

            if (!AnimationHandler.HasAnims(level)) { return; }

            AnimConfig config;
            XmlSerializer reader = new XmlSerializer(typeof(AnimConfig));
            using (var sww = new StreamReader(String.Format("Animations/{0}+animationProps.xml", level.name)))
            {
                config = (AnimConfig)reader.Deserialize(sww);
            }

            AnimationHandler.dictActiveLevels[level.name].bKiller = config.Killer;
            AnimationHandler.dictActiveLevels[level.name].SetSpeed(config.Speed);
        }

        // Save properties file for animations as an XML file
        public static void SaveConfig(Level level)
        {
            if (!AnimationHandler.HasAnims(level)) { return; }

            MapAnimation mapAnimation = AnimationHandler.dictActiveLevels[level.name];

            var config = new AnimConfig
            {
                Killer = mapAnimation.bKiller,
                Speed = mapAnimation._speed
            };

            XmlSerializer writer = new XmlSerializer(typeof(AnimConfig));
            using (var sw = new StreamWriter(String.Format("Animations/{0}+animationProps.xml", level.name)))
            {
                writer.Serialize(sw, config);
                string xml = sw.ToString();
            }
        }

        // Renames one [level]+animation.txt to another
        private void RenameAnimation(string srcMap, string dstMap)
        {
            try
            {
                if (File.Exists(String.Format("Animations/{0}+animation.txt", srcMap)))
                {
                    File.Move(String.Format("Animations/{0}+animation.txt", srcMap),
                        String.Format("Animations/{0}+animation.txt", dstMap));
                }
                else
                {
                    return;
                }
            }
            catch (Exception e)
            {
                Logger.Log(LogType.Error, String.Format("Could not copy Animations/{0}+animation.txt" +
                    " into Animations/{1}+animation.txt", srcMap, dstMap));
                Logger.Log(LogType.Error, e.StackTrace);
                return;
            }
        }

        // Renames one [level]+animationProps.xml to another
        private void RenameAnimationConfig(string srcMap, string dstMap)
        {
            try
            {
                if (File.Exists(String.Format("Animations/{0}+animationProps.xml", srcMap)))
                {
                    File.Move(String.Format("Animations/{0}+animationProps.xml", srcMap),
                        String.Format("Animations/{0}+animationProps.xml", dstMap));
                }
                else
                {
                    return;
                }
            }
            catch (Exception e)
            {
                Logger.Log(LogType.Error, String.Format("Could not copy Animations/{0}+animationProps.xml" +
                    " into Animations/{1}+animationProps.xml", srcMap, dstMap));
                Logger.Log(LogType.Error, e.StackTrace);
                return;
            }
        }

        // Write the animation thus far to [level]+animation.txt in ./Animations
        public static void SaveAnimation(Level level)
        {
            if (!LevelInfo.Loaded.Contains(level))
            {
                return;
            }

            if (!AnimationHandler.HasAnims(level))
            {
                return;
            }

            MapAnimation mapAnim = AnimationHandler.dictActiveLevels[level.name];

            if (mapAnim._numLoops == 0)
            {
                return;
            }

            List<string> lines = new List<string>();
            foreach (AnimBlock animBlock in mapAnim._blocks)
            {
                foreach (var kvp in animBlock._loopList)
                {
                    // File is ordered as <x> <y> <z> <index> <interval> <duration> <start> <end> <blockID>
                    lines.Add(String.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8}", animBlock._x, animBlock._y, animBlock._z, kvp.Key, kvp.Value._interval, kvp.Value._duration, kvp.Value._startTick, kvp.Value._endTick, kvp.Value._block));
                }
            }

            File.WriteAllLines(String.Format("Animations/{0}+animation.txt", level.name), lines.ToArray());
        }

        // Write the animation thus far to [level]+animation.txt in ./AnimationsBackup  TODO: Repetition of code, but not sure of an abstract is necessary here
        public static void SaveAnimationBackup(Level level)
        {
            if (!LevelInfo.Loaded.Contains(level))
            {
                return;
            }

            if (!AnimationHandler.HasAnims(level))
            {
                return;
            }

            MapAnimation mapAnim = AnimationHandler.dictActiveLevels[level.name];

            if (mapAnim._numLoops == 0)
            {
                return;
            }

            List<string> lines = new List<string>();
            foreach (AnimBlock animBlock in mapAnim._blocks)
            {
                foreach (var kvp in animBlock._loopList)
                {
                    // File is ordered as <x> <y> <z> <index> <interval> <duration> <start> <end> <blockID>
                    lines.Add(String.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8}", animBlock._x, animBlock._y, animBlock._z, kvp.Key, kvp.Value._interval, kvp.Value._duration, kvp.Value._startTick, kvp.Value._endTick, kvp.Value._block));
                }
            }

            File.WriteAllLines(String.Format("AnimationsBackup/{0}+animation.txt", level.name), lines.ToArray());
        }

        // Deletes the animation file [level]+animation.txt in ./Animations if it exists
        public static void ConditionalDeleteAnimationFile(string level)
        {
            if (AnimationExists(level))
            {
                try
                {
                    File.Delete(String.Format("Animations/{0}+animation.txt", level));
                }
                catch (Exception e)
                {
                    Logger.Log(LogType.Error, String.Format("Failed to delete file \"Animations/{0}+animation.txt\"", level));
                    Logger.Log(LogType.Error, e.StackTrace);
                }
            }
        }


        // Deletes the animation config file [level]+animationProps.xml in ./Animations if it exists
        public static void ConditionalDeleteAnimationConfigFile(string level)
        {
            if (AnimationExists(level))
            {
                try
                {
                    File.Delete(String.Format("Animations/{0}+animationProps.xml", level));
                }
                catch (Exception e)
                {
                    Logger.Log(LogType.Error, String.Format("Failed to delete file \"Animations/{0}+animationProps.xml\"", level));
                    Logger.Log(LogType.Error, e.StackTrace);
                }
            }
        }

        // Checks if [level]+animations.txt exists in ./Animations
        public static bool AnimationExists(string level)
        {
            return File.Exists(String.Format("Animations/{0}+animation.txt", level));
        }

    }

    /**********
     * CONFIG *
     **********/

    public sealed class AnimConfig
    {
        public ushort Speed { get; set; }
        public bool Killer { get; set; }
    }

    /*********************
     * ANIMATION HANDLER *
     *********************/

    // Handles animation scheduling across maps as well as adding loops to/removing loops from animations in these maps
    // Note that we need to keep maps in this static class, because maps are not passed around by reference but rather many instances
    // Are made for each player, so that using the ExtrasCollection for each map will not be viable
    public static class AnimationHandler
    {
        public static Dictionary<string, MapAnimation> dictActiveLevels = new Dictionary<string, MapAnimation>();  // Levels with map animations

        internal static void Activate()
        {
            foreach (var kvp in dictActiveLevels)
            {
                kvp.Value.Activate();
            }
        }

        internal static void Deactivate()
        {
            foreach (var kvp in dictActiveLevels)
            {
                kvp.Value.Deactivate();
            }
            dictActiveLevels.Clear();
        }

        // Keeps track of which (loaded) maps have animations i.e. which maps to handle
        internal static bool HasAnims(Level level)
        {
            return dictActiveLevels.ContainsKey(level.name);
        }

        // Sets the map animation for a level
        internal static void SetAnims(Level level, MapAnimation mapAnim)
        {
            dictActiveLevels[level.name] = mapAnim;
            dictActiveLevels[level.name].Activate();
        }

        // Removes a level from the active levels
        internal static void RemoveFromActiveLevels(Level level)
        {
            if (HasAnims(level))
            {
                dictActiveLevels[level.name].Deactivate();
                dictActiveLevels.Remove(level.name);
            }
        }

        // Sends current animation frame globally, for everyone on the level
        internal static void SendCurrentFrame(Level level)
        {
            foreach (Player pl in level.players)
            {
                SendCurrentFrame(pl, level);
            }
        }

        // Sends current animation frame. Useful when marking or breaking a block, or on level join event
        internal static void SendCurrentFrame(Player p, Level level)
        {
            BufferedBlockSender sender = new BufferedBlockSender(p);
            MapAnimation mapAnimation;
            if (HasAnims(level))
            {
                mapAnimation = dictActiveLevels[level.name];
            }
            else
            {
                return;
            }

            foreach (AnimBlock aBlock in mapAnimation._blocks)
            {
                BlockID CurrentBlock = GetCurrentBlock(aBlock._loopList, mapAnimation._currentTick);
                if (CurrentBlock == BlockID.MaxValue)
                {
                    CurrentBlock = level.GetBlock(aBlock._x, aBlock._y, aBlock._z);
                }

                sender.Add(level.PosToInt(aBlock._x, aBlock._y, aBlock._z), CurrentBlock);
            }

            if (sender.count > 0)
            {
                sender.Flush();
            }
        }

        // Sends current frame block identity for a specific block
        internal static void SendCurrentFrameBlock(Player p, ushort x, ushort y, ushort z)
        {
            MapAnimation mapAnimation;
            if (HasAnims(p.level))
            {
                mapAnimation = dictActiveLevels[p.level.name];
            }
            else
            {
                return;
            }

            foreach (AnimBlock aBlock in mapAnimation._blocks)
            {
                if (aBlock._x == x && aBlock._y == y && aBlock._z == z)
                {
                    BlockID CurrentBlock = GetCurrentBlock(aBlock._loopList, mapAnimation._currentTick);
                    if (CurrentBlock != BlockID.MaxValue)
                    {
                        p.SendBlockchange(aBlock._x, aBlock._y, aBlock._z, CurrentBlock);
                    }
                    else
                    {
                        p.RevertBlock(aBlock._x, aBlock._y, aBlock._z);
                    }
                    return;
                }
            }
        }

        // Handles animation on a single map
        internal static void Update(SchedulerTask task)
        {
            Level currentLevel = (Level)task.State;
            MapAnimation mapAnimation = dictActiveLevels[currentLevel.name];
            if (mapAnimation.bRunning && !mapAnimation.bEditingLock)
            {
                foreach (AnimBlock animBlock in mapAnimation._blocks)
                {
                    BlockID prevBlock = animBlock._currentBlock;    // Previous frame's block
                    BlockID currentBlock = GetCurrentBlock(animBlock._loopList, mapAnimation._currentTick); // Current frame's block

                    // Special value saying that the loop is off. Revert to usual block
                    if (currentBlock == UInt16.MaxValue)
                    {
                        currentBlock = currentLevel.GetBlock(animBlock._x, animBlock._y, animBlock._z);
                    }

                    if (currentBlock == prevBlock) // Don't have to send block changes if the block doesn't change
                    {
                        continue;
                    }

                    int index = currentLevel.PosToInt((ushort)animBlock._x, (ushort)animBlock._y, (ushort)animBlock._z);

                    mapAnimation.buffer.Add(index, currentBlock);

                    animBlock._currentBlock = currentBlock;
                }

                // Handle remaining blocks
                if (mapAnimation.buffer.count != 0)
                {
                    mapAnimation.buffer.Flush();
                }

                // Handle death
                if (mapAnimation.bKiller)
                {
                    foreach (Player pl in currentLevel.players)
                    {
                        Walkthrough(pl, pl.ModelBB.OffsetPosition(pl.Pos), mapAnimation._currentTick, mapAnimation);
                    }
                }

                mapAnimation._currentTick += 1;

                if (mapAnimation._currentTick == ushort.MaxValue)
                {
                    mapAnimation._currentTick = 0;
                }
            }
        }

        // Handles walkthrough, checks if player dies
        internal static void Walkthrough(Player p, AABB bb, ushort tick, MapAnimation mapAnim)
        {
            Vec3S32 min = bb.BlockMin, max = bb.BlockMax;
            bool hitWalkthrough = false;

            for (int y = min.Y; y <= max.Y; y++)
                for (int z = min.Z; z <= max.Z; z++)
                    for (int x = min.X; x <= max.X; x++)
                    {
                        ushort xP = (ushort)x, yP = (ushort)y, zP = (ushort)z;
                        BlockID block = AnimationHandler.GetCurrentBlock(mapAnim, xP, yP, zP, tick);
                        if (block == System.UInt16.MaxValue) continue;

                        AABB blockBB = Block.BlockAABB(block, p.level).Offset(x * 32, y * 32, z * 32);
                        if (!AABB.Intersects(ref bb, ref blockBB)) continue;

                        // We can activate only one walkthrough block per movement
                        if (!hitWalkthrough)
                        {
                            HandleWalkthrough handler = p.level.WalkthroughHandlers[block];
                            if (handler != null && handler(p, block, xP, yP, zP))
                            {
                                hitWalkthrough = true;
                            }
                        }

                        // Some blocks will cause death of players
                        if (!p.level.Props[block].KillerBlock) continue;
                        if (block == Block.Train && p.trainInvincible) continue;
                        if (p.level.Config.KillerBlocks) p.HandleDeath(block);
                    }
        }

        // Gets the currently visible block at a specific location
        private static BlockID GetCurrentBlock(MapAnimation mapAnim, ushort x, ushort y, ushort z, ushort tick)
        {
            foreach (AnimBlock animBlock in mapAnim._blocks)
            {
                if (animBlock._x == x && animBlock._y == y && animBlock._z == z)
                {
                    return GetCurrentBlock(animBlock._loopList, tick);
                }
            }
            // Return this if nothing is there
            return System.UInt16.MaxValue;
        }

        // Gets the currently visible block given the tick across an array of loops. Returns 65535 if no block is visible at that frame
        private static BlockID GetCurrentBlock(SortedList<ushort, AnimLoop> loops, ushort tick)
        {
            BlockID loopActiveBlock;
            foreach (var kvp in loops)  // kvp = (index, AnimLoop). Note that this loops from lowest to highest index automatically
            {
                loopActiveBlock = CurrentBlock(kvp.Value, tick);    // The block visible during the current loop
                if (loopActiveBlock != System.UInt16.MaxValue)
                {
                    return loopActiveBlock;
                }
            }

            return System.UInt16.MaxValue;
        }

        // Gets the currently visible block for a single loop. Returns 65535 if the loop is "off" in a frame
        private static BlockID CurrentBlock(AnimLoop loop, ushort tick)
        {

            if (tick < loop._startTick)
            {
                return System.UInt16.MaxValue;
            }

            try {
                if (tick > loop._endTick)
                {
                    if (((loop._endTick - loop._startTick) % loop._interval) < loop._duration)       // If the block is active during the loop
                    {
                        return loop._block;
                    }
                    return System.UInt16.MaxValue;
                }

                if (((tick - loop._startTick) % loop._interval) < loop._duration)       // If the block is active during the loop
                {
                    return loop._block;
                }
            } catch (System.DivideByZeroException e) {
                // No more error log spam please - goodly
                return System.UInt16.MaxValue;
            }

            return System.UInt16.MaxValue;
        }

        // Initializes a map animation if it does not already exist. Adds it to the active levels
        internal static void ConditionalAddMapAnimation(Level level)
        {
            if (!HasAnims(level))
            {
                MapAnimation mapAnimation = new MapAnimation(true, 0, 0, level, 100, false)
                {
                    _blocks = new List<AnimBlock>()
                };

                SetAnims(level, mapAnimation);
            }
        }

        // Remove a map animation if it exists. Removes it from the active levels
        private static void ConditionalRemoveMapAnimation(Level level)
        {
            if (HasAnims(level))
            {
                RemoveFromActiveLevels(level);
            }
        }

        // Places a loop in a level. If "all" is set to true, replaces all loops for a block
        internal static void Place(Level level, ushort x, ushort y, ushort z, ushort idx, ushort interval, ushort duration, short startTick, ushort endTick, BlockID block, bool all, bool append)
        {
            ConditionalAddMapAnimation(level);

            MapAnimation mapAnimation = AnimationHandler.dictActiveLevels[level.name];
            mapAnimation.bEditingLock = true;

            if (mapAnimation._numLoops == ushort.MaxValue)
            {
                mapAnimation.bEditingLock = false;
                return;
            }

            foreach (AnimBlock animB in mapAnimation._blocks)
            {
                if (animB._x == x && animB._y == y && animB._z == z)
                {
                    AnimLoop loop = new AnimLoop(interval, duration, startTick, endTick, block);

                    if (all)    // If replacing
                    {
                        mapAnimation._numLoops -= (ushort)animB._loopList.Count;
                        animB._loopList.Clear();
                    }
                    else if (!all && append)     // If appending
                    {
                        idx = (ushort)(animB._loopList.Keys.Max() + 1);
                    }
                    else if (!all && !append)  // If prepending
                    {
                        if (animB._loopList.Keys.Min() == 1)
                        {
                            // Push everything forward by 1
                            foreach (ushort k in animB._loopList.Keys.Reverse())
                            {
                                AnimLoop copy = new AnimLoop(animB._loopList[k]._interval, animB._loopList[k]._duration,
                                animB._loopList[k]._startTick, animB._loopList[k]._endTick, animB._loopList[k]._block);
                                animB._loopList[(ushort)(k + 1)] = copy;
                                animB._loopList.Remove(k);
                            }

                            animB._loopList.Remove(1);
                        }

                        idx = (ushort)(animB._loopList.Keys.Min() - 1);
                    }

                    // Replace loop if it already exists with the given index. Otherwise overwrite it
                    if (animB._loopList.Keys.Contains(idx))
                    {
                        animB._loopList[idx] = loop;
                    }
                    else
                    {
                        animB._loopList.Add(idx, loop);
                        mapAnimation._numLoops += 1;
                    }

                    mapAnimation.bEditingLock = false;
                    return;
                }
            }

            // If we're here, it means we're not overwriting a block but creating a new one
            AnimBlock animBlock = new AnimBlock(x, y, z, block);
            animBlock._loopList.Add(1, new AnimLoop(interval, duration, startTick, endTick, block));

            mapAnimation._blocks.Add(animBlock);
            mapAnimation._numLoops += 1;

            mapAnimation.bEditingLock = false;
        }

        // Deletes a loop in a level. If all is true, deletes all loops for a block
        internal static void Delete(Level level, ushort x, ushort y, ushort z, ushort idx, bool all, bool deleteBlock)
        {
            if (!AnimationHandler.HasAnims(level)) return;

            MapAnimation mapAnimation = AnimationHandler.dictActiveLevels[level.name];
            mapAnimation.bEditingLock = true;

            // Looks like a massive loop but takes no more than O(number of animation blocks) operations
            foreach (AnimBlock animBlock in mapAnimation._blocks)
            {
                if (!(animBlock._x == x && animBlock._y == y && animBlock._z == z)) { continue; }

                if (all)    // Remove all loops at the location
                {
                    mapAnimation._numLoops -= (ushort)animBlock._loopList.Count;
                    if (mapAnimation._numLoops <= 0)
                    {
                        ConditionalRemoveMapAnimation(level);
                    }

                    mapAnimation._blocks.Remove(animBlock);
                    mapAnimation.bEditingLock = false;
                    break;
                }
                else
                {
                    foreach (var kvp in animBlock._loopList)    // Find the loop with the specified index and remove only that
                    {
                        if ((kvp.Key == idx && deleteBlock == false) || (kvp.Value._block == idx && deleteBlock == true))       // Deletes by block if deleteBlock is true, else by index
                        {
                            animBlock._loopList.Remove(kvp.Key);

                            if (animBlock._loopList.Count == 0)
                            {
                                mapAnimation._blocks.Remove(animBlock);
                            }

                            mapAnimation._numLoops -= 1;
                            if (mapAnimation._numLoops <= 0)
                            {
                                ConditionalRemoveMapAnimation(level);
                            }
                            break;
                        }
                    }
                    break;
                }
            }
            mapAnimation.bEditingLock = false;
        }

        // Adds a loop in a level. Creates animation block for this loop if it does not exist already. If loop index already exists, overwrites it
        internal static void AddLoop(Level level, ushort x, ushort y, ushort z, ushort index, ushort interval, ushort duration, short start, ushort end, BlockID block)
        {
            ConditionalAddMapAnimation(level);

            MapAnimation mapAnimation = dictActiveLevels[level.name];

            AnimLoop loop = new AnimLoop(interval, duration, start, end, block);

            // Add loop to an existing animation block if it exists
            foreach (AnimBlock aBlock in mapAnimation._blocks)
            {
                if (aBlock._x == x && aBlock._y == y && aBlock._z == z)
                {
                    if (aBlock._loopList.ContainsKey(index))
                    {
                        aBlock._loopList[index] = new AnimLoop(interval, duration, start, end, block);
                        return;
                    }
                    else
                    {
                        aBlock._loopList.Add(index, loop);
                        mapAnimation._numLoops += 1;
                        return;
                    }
                }
            }

            // Create the animation block then add the loop
            AnimBlock animBlock = new AnimBlock(x, y, z, block);
            animBlock._loopList.Add(index, loop);
            mapAnimation._blocks.Add(animBlock);

            mapAnimation._numLoops += 1;
        }
    }

    public sealed class CmdAnimation : Command2
    {
        public override string name { get { return "Animation"; } }
        public override string shortcut { get { return "anim"; } }
        public override string type { get { return CommandTypes.Other; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Guest; } }

        public override CommandPerm[] ExtraPerms {
            get { return new[] { new CommandPerm(LevelPermission.Guest, "can bypass the realm owner permission check") }; }
        }

        public override void Use(Player p, string message)
        {
            //Cache level so that the player cannot switch levels after the permission check has ran but before the cmd runs
            Level level = p.level;

            if (!(HasExtraPerm(p, p.Rank, 1) || LevelInfo.IsRealmOwner(p.name, level.name))) {
                p.Message("You can only use &T/{0} &Sif you are the map owner.", name);
                return;
            }
            if (!level.BuildAccess.CheckAllowed(p))
            {
                p.Message("You do not have permissions to use animations on this level.");
                return;
            }

            string[] args = message.ToLower().SplitSpaces();

            AnimationArgs animArgs = new AnimationArgs();

            /******************
             * COMMAND PARSER *
             ******************/

            /* EXTRAS FLAGS */
            bool bCuboid = false; bool bAll = true; bool bAppend = false;                   // Default behavior
            List<string> zSynms = new List<string> { "z", "cuboid" };                       // Cuboid synonynms
            List<string> appSynms = new List<string> { "a", "append" };                     // Append/prepend synonyms
            List<string> prepSynms = new List<string> { "p", "prepend" };                   // Append/prepend synonyms

            foreach (string arg in args)
            {
                if (zSynms.Contains(arg))
                {
                    bCuboid = true;
                }

                if (appSynms.Contains(arg))
                {
                    bAll = false;
                    bAppend = true;
                }

                if (prepSynms.Contains(arg))
                {
                    bAll = false;
                    bAppend = false;
                }
            }

            if (!bCuboid && bAll)
            {
                animArgs._commandMode = (ushort)AnimCommandMode.Default;
            }
            else if (bCuboid && bAll)
            {
                animArgs._commandMode = (ushort)AnimCommandMode.DefaultCuboid;
            }
            else if (!bCuboid && bAppend)
            {
                animArgs._commandMode = (ushort)AnimCommandMode.Append;
            }
            else if (bCuboid && bAppend)
            {
                animArgs._commandMode = (ushort)AnimCommandMode.AppendCuboid;
            }
            else if (!bCuboid && !bAppend)
            {
                animArgs._commandMode = (ushort)AnimCommandMode.Prepend;
            }
            else if (bCuboid && !bAppend)
            {
                animArgs._commandMode = (ushort)AnimCommandMode.PrependCuboid;
            }

            args = (args.Where(s => !(zSynms.Contains(s) || appSynms.Contains(s) || prepSynms.Contains(s)))).ToList().ToArray();   // Keep only the non-cuboid, non-append/prepend related args

            // Add the map animation if it doesn't exist
            if (!AnimationHandler.HasAnims(level))
            {
                AnimationHandler.ConditionalAddMapAnimation(level);
            }

            /* COMMAND SWITCH */
            switch (args.Length)
            {
                case 1: // "/anim stop", "/anim start", "/anim delete", "/anim save", "/anim restart", "/anim info", "/anim copy", "/anim paste", "/anim reverse", "/anim cut", "/anim backup", "/anim killer" (and just "/anim" is also considered length 1)
                    if (args[0] == "stop")          // "/anim stop"
                    {
                        StopAnim(level);
                        p.Message("Stopped animation");
                        AnimationHandler.SendCurrentFrame(level);
                    }
                    else if (args[0] == "start")    // "/anim start"
                    {
                        StartAnim(level);
                        p.Message("Started animation");
                        AnimationHandler.SendCurrentFrame(level);
                    }
                    else if (args[0] == "save")     // "/anim save"
                    {
                        AnimationsPlugin.SaveAnimation(level);
                        AnimationsPlugin.SaveConfig(level);
                        level.Message("Saved animations");
                    }
                    else if (args[0] == "delete")   // "/anim delete"
                    {
                        p.Message("Mark where you want to delete animations");

                        animArgs._commandCode = (ushort)AnimCommandCode.Delete;

                        p.MakeSelection(bCuboid ? 2 : 1, animArgs, PlacedMark);
                    }
                    else if (args[0] == "info")     // "/anim info"
                    {
                        p.Message("Mark the block you want info of");

                        animArgs._commandCode = (ushort)AnimCommandCode.Info;

                        p.MakeSelection(1, animArgs, PlacedMark);
                    }
                    else if (args[0] == "restart")  // "/anim restart"
                    {
                        p.Message("Restarted animation");

                        AnimationHandler.dictActiveLevels[level.name]._currentTick = 1;
                        AnimationHandler.SendCurrentFrame(level);
                    }
                    else if (args[0] == "copy")       // "/anim copy"
                    {
                        p.Message("Mark the bounds of your copy");
                        animArgs._commandCode = (ushort)AnimCommandCode.Copy;

                        p.MakeSelection(2, animArgs, PlacedMark);
                    }
                    else if (args[0] == "cut")    // "/anim cut"
                    {
                        p.Message("Mark the bounds of your cut");
                        animArgs._commandCode = (ushort)AnimCommandCode.Cut;

                        p.MakeSelection(2, animArgs, PlacedMark);
                    }
                    else if (args[0] == "paste")      // "/anim paste"
                    {
                        p.Message("Mark where you want to paste your animation from");
                        animArgs._commandCode = (ushort)AnimCommandCode.Paste;
                        animArgs._startTick = 0;    // Delay

                        p.MakeSelection(1, animArgs, PlacedMark);
                    }
                    else if (args[0] == "reverse")    // "/anim reverse"
                    {
                        p.Message("Mark where you want to reverse your animation");
                        animArgs._commandCode = (ushort)AnimCommandCode.Reverse;

                        p.MakeSelection(bCuboid ? 2 : 1, animArgs, PlacedMark);
                    }
                    else if (args[0] == "backup")     // "/anim backup"
                    {
                        AnimationsPlugin.SaveAnimationBackup(level);
                        p.Message("Created backup");
                    }
                    else if (args[0] == "killer")       // "/anim killer"
                    {
                        if (AnimationHandler.dictActiveLevels[level.name].bKiller)
                        {
                            AnimationHandler.dictActiveLevels[level.name].bKiller = false;
                            p.Message("Killer blockprops are off for animations");
                        }
                        else
                        {
                            AnimationHandler.dictActiveLevels[level.name].bKiller = true;
                            p.Message("Killer blockprops are on for animations");
                        }
                    }
                    else
                    {
                        Help(p);
                        return;
                    }
                    break;
                case 2: // "/anim [interval] [duration]", "/anim delete [num]", "/anim at [tick]", "/anim paste [delay]", "/anim shift [delay]", "/anim speed [ms]", '/anim remove all"
                    ushort interval, duration, num, tick; short delay;
                    if (ushort.TryParse(args[0], out interval) && ushort.TryParse(args[1], out duration))    // "/anim [interval] [duration]"
                    {
                        if (duration > interval)
                        {
                            p.Message("Duration cannot be greater than interval!");
                            return;
                        }
                        else if (duration == 0)
                        {
                            p.Message("Duration cannot be 0");
                            return;
                        }

                        p.Message("Mark where you want to place your animation");

                        animArgs._commandCode = (ushort)AnimCommandCode.IntervalDuration;
                        animArgs._duration = duration;
                        animArgs._interval = interval;
                        animArgs._startTick = 0;
                        animArgs._endTick = ushort.MaxValue;
                        animArgs._idx = 1;

                        p.MakeSelection(bCuboid ? 2 : 1, animArgs, PlacedMark);
                    }
                    else if (args[0] == "delete" && ushort.TryParse(args[1], out num))                   // "/anim delete [num]"
                    {
                        p.Message("Mark where you want to delete your animation");

                        animArgs._commandCode = (ushort)AnimCommandCode.DeleteNum;
                        animArgs._idx = num;

                        p.MakeSelection(bCuboid ? 2 : 1, animArgs, PlacedMark);
                    }
                    else if (args[0] == "at" && ushort.TryParse(args[1], out tick))        // "/anim at [tick]"
                    {
                        p.Message(String.Format("Set tick to {0}", tick.ToString()));

                        AnimationHandler.dictActiveLevels[level.name]._currentTick = tick;
                        AnimationHandler.SendCurrentFrame(level);
                    }
                    else if (args[0] == "paste" && short.TryParse(args[1], out delay))    // "/anim paste [delay]"
                    {
                        p.Message("Mark where you want to paste your animation from");

                        animArgs._commandCode = (ushort)AnimCommandCode.PasteDelay;
                        animArgs._startTick = delay; // Delay

                        p.MakeSelection(1, animArgs, PlacedMark);
                    }
                    else if (args[0] == "shift" && short.TryParse(args[1], out delay))    // "/anim shift [delay]"
                    {
                        p.Message("Mark where you want to delay your animation");

                        animArgs._commandCode = (ushort)AnimCommandCode.Shift;
                        animArgs._startTick = delay;

                        p.MakeSelection(bCuboid ? 2 : 1, animArgs, PlacedMark);
                    }
                    else if (args[0] == "speed" && ushort.TryParse(args[1], out num))     // "/anim speed [ms]"
                    {
                        if (!AnimationHandler.HasAnims(level)) { return; }

                        if (num < 50)
                        {
                            p.Message("Num cannot be less than 50");
                            return;
                        }

                        AnimationHandler.dictActiveLevels[level.name].SetSpeed(num);
                        p.Message(String.Format("Speed changed to {0}", num.ToString()));
                    }
                    else if (args[0] == "remove" && args[1] == "all")         // "/anim remove all"
                    {
                        AnimationHandler.dictActiveLevels[level.name]._blocks = new List<AnimBlock>();
                        AnimationHandler.SendCurrentFrame(level);

                        AnimationsPlugin.ConditionalDeleteAnimationFile(level.name);
                        AnimationsPlugin.ConditionalDeleteAnimationConfigFile(level.name);

                        AnimationHandler.RemoveFromActiveLevels(level);
                    }
                    else
                    {
                        Help(p);
                        return;
                    }
                    break;
                case 3: // "/anim swap [num 1] [num 2]", "/anim [start] [interval] [duration]", "/anim delete block [block]"
                    ushort idx1, idx2; short start;
                    if (args[0] == "swap" && ushort.TryParse(args[1], out idx1) && ushort.TryParse(args[2], out idx2))   // "/anim swap [num 1] [num 2]"
                    {
                        p.Message("Mark where you want to perform your swap");

                        animArgs._commandCode = (ushort)AnimCommandCode.SwapNum1Num2;
                        animArgs._idx = idx1;
                        animArgs._idx2 = idx2;

                        p.MakeSelection(bCuboid ? 2 : 1, animArgs, PlacedMark);
                    }
                    else if (short.TryParse(args[0], out start) && ushort.TryParse(args[1], out interval) && ushort.TryParse(args[2], out duration))    // "/anim [start] [interval] [duration]"
                    {
                        if (duration > interval)
                        {
                            p.Message("Duration cannot be greater than interval!");
                            return;
                        }
                        else if (duration == 0)
                        {
                            p.Message("Duration cannot be 0!");
                            return;
                        }

                        p.Message("Mark where you want to place your animation");

                        animArgs._commandCode = (ushort)AnimCommandCode.StartIntervalDuration;
                        animArgs._interval = interval;
                        animArgs._duration = duration;
                        animArgs._startTick = start;
                        animArgs._endTick = ushort.MaxValue;
                        animArgs._idx = 1;

                        p.MakeSelection(bCuboid ? 2 : 1, animArgs, PlacedMark);
                    }
                    else if (args[0] == "delete" && args[1] == "block" && Block.Parse(p, args[2]) != Block.Invalid)    // "/anim delete block [block]"
                    {
                        p.Message("Mark where you want to delete your animation");

                        animArgs._commandCode = (ushort)AnimCommandCode.DeleteBlock;
                        animArgs._idx = Block.ToRaw(Block.Parse(p, args[2]));

                        p.MakeSelection(bCuboid ? 2 : 1, animArgs, PlacedMark);
                    }
                    else
                    {
                        Help(p);
                        return;
                    }
                    break;
                default:    // "/anim [start] [block 1] [duration 1] [block 2] [duration 2]..."
                    ushort temp; animArgs._blockList = new List<BlockID>(); animArgs._durationList = new List<BlockID>();
                    if (args.Length >= 5)   // "/anim [start] [block 1] [duration 1] [block 2] [duration 2]..."
                    {
                        if (!short.TryParse(args[0], out start))
                        {
                            Help(p);
                            break;
                        }

                        for (int i = 1; i < args.Length - 1; i += 2)
                        {
                            if (Block.Parse(p, args[i]) != Block.Invalid && ushort.TryParse(args[i + 1], out temp))
                            {
                                animArgs._blockList.Add(Block.Parse(p, args[i]));
                                animArgs._durationList.Add(temp);
                            }
                            else
                            {
                                Help(p);
                                return;
                            }
                        }
                        p.Message("Mark where you want to place your animation");

                        animArgs._commandCode = (ushort)AnimCommandCode.BlockList;
                        animArgs._startTick = start;

                        p.MakeSelection(bCuboid ? 2 : 1, animArgs, PlacedMark);
                    }
                    else
                    {
                        Help(p);
                        return;
                    }
                    break;
            }
        }

        /*******************
        * HELPER FUNCTIONS *
        ********************/

        // Stores info about which command is being accessed. Maybe not the shortest, but it keeps things organized
        public enum AnimCommandCode : ushort
        {
            BlockList = 1,
            IntervalDuration = 2,
            StartIntervalDuration = 3,
            Delete = 4,
            AppendIntervalDuration = 5,
            AppendStartIntervalDuration = 6,
            SwapNum1Num2 = 7,
            DeleteNum = 8,
            Copy = 9,
            Paste = 10,
            PasteDelay = 11,
            Info = 12,
            DeleteBlock = 13,
            Shift = 14,
            Reverse = 15,
            Cut = 16
        }

        public enum AnimCommandMode : ushort
        {
            Default = 1,
            DefaultCuboid = 2,
            Append = 3,
            Prepend = 4,
            AppendCuboid = 5,
            PrependCuboid = 6
        }

        // Information needed when we mark a block for placing/deleting
        private struct AnimationArgs
        {
            public short _startTick;
            public ushort _interval;
            public ushort _duration;
            public ushort _idx;
            public ushort _idx2;    // Only reserved for swapping
            public ushort _commandCode; // Command code (see above enum)
            public ushort _commandMode; // Command Mode (see above enum)
            public ushort _endTick;     // NOTE: Legacy
            public List<ushort> _durationList;
            public List<BlockID> _blockList;
        }

        // Once a block is placed this is called. See the definition of AnimationArgs (above) for state members
        private bool PlacedMark(Player p, Vec3S32[] marks, object state, BlockID block)
        {
            if (!p.level.BuildAccess.CheckAllowed(p))
            {
                p.Message("You do not have permissions to use animations on this level.");
                return true;
            }

            ushort x = (ushort)marks[0].X, y = (ushort)marks[0].Y, z = (ushort)marks[0].Z;
            ushort x2 = 0, y2 = 0, z2 = 0;
            if (marks.Length > 1)
            {
                x2 = (ushort)marks[1].X; y2 = (ushort)marks[1].Y; z2 = (ushort)marks[1].Z;
            }

            AnimationArgs animArgs = (AnimationArgs)state;

            switch (animArgs._commandCode + 100 * animArgs._commandMode)    // Cheap way to pack both arguments into one switch statement
            {
                // "/anim [start] [block 1] [interval 1] [duration 1] [block 2] [interval 2] [duration 2]..."
                case (ushort)AnimCommandCode.BlockList + 100 * (ushort)AnimCommandMode.Default:
                    PlaceBlockList(p, x, y, z, animArgs._startTick, animArgs._blockList, animArgs._durationList, true, true);
                    break;
                case (ushort)AnimCommandCode.BlockList + 100 * (ushort)AnimCommandMode.DefaultCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, true, true);
                    break;
                case (ushort)AnimCommandCode.BlockList + 100 * (ushort)AnimCommandMode.Append:
                    PlaceBlockList(p, x, y, z, animArgs._startTick, animArgs._blockList, animArgs._durationList, false, true);
                    break;
                case (ushort)AnimCommandCode.BlockList + 100 * (ushort)AnimCommandMode.AppendCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, false, true);
                    break;
                case (ushort)AnimCommandCode.BlockList + 100 * (ushort)AnimCommandMode.Prepend:
                    PlaceBlockList(p, x, y, z, animArgs._startTick, animArgs._blockList, animArgs._durationList, false, false);
                    break;
                case (ushort)AnimCommandCode.BlockList + 100 * (ushort)AnimCommandMode.PrependCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, false, false);
                    break;

                // "/anim [interval] [duration]"
                case (ushort)AnimCommandCode.IntervalDuration + 100 * (ushort)AnimCommandMode.Default:
                    PlaceAnimation(p, x, y, z, animArgs, block, true, false);
                    break;
                case (ushort)AnimCommandCode.IntervalDuration + 100 * (ushort)AnimCommandMode.DefaultCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, true, false);
                    break;
                case (ushort)AnimCommandCode.IntervalDuration + 100 * (ushort)AnimCommandMode.Append:
                    PlaceAnimation(p, x, y, z, animArgs, block, false, true);
                    break;
                case (ushort)AnimCommandCode.IntervalDuration + 100 * (ushort)AnimCommandMode.AppendCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, false, true);
                    break;
                case (ushort)AnimCommandCode.IntervalDuration + 100 * (ushort)AnimCommandMode.Prepend:
                    PlaceAnimation(p, x, y, z, animArgs, block, false, false);
                    break;
                case (ushort)AnimCommandCode.IntervalDuration + 100 * (ushort)AnimCommandMode.PrependCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, false, false);
                    break;

                // "/anim [start] [interval] [duration]"
                case (ushort)AnimCommandCode.StartIntervalDuration + 100 * (ushort)AnimCommandMode.Default:
                    PlaceAnimation(p, x, y, z, animArgs, block, true, false);
                    break;
                case (ushort)AnimCommandCode.StartIntervalDuration + 100 * (ushort)AnimCommandMode.DefaultCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, true, false);
                    break;
                case (ushort)AnimCommandCode.StartIntervalDuration + 100 * (ushort)AnimCommandMode.Append:
                    PlaceAnimation(p, x, y, z, animArgs, block, false, true);
                    break;
                case (ushort)AnimCommandCode.StartIntervalDuration + 100 * (ushort)AnimCommandMode.AppendCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, false, true);
                    break;
                case (ushort)AnimCommandCode.StartIntervalDuration + 100 * (ushort)AnimCommandMode.Prepend:
                    PlaceAnimation(p, x, y, z, animArgs, block, false, false);
                    break;
                case (ushort)AnimCommandCode.StartIntervalDuration + 100 * (ushort)AnimCommandMode.PrependCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, false, false);
                    break;

                // "/anim delete"
                case (ushort)AnimCommandCode.Delete + 100 * (ushort)AnimCommandMode.Default:
                    DeleteAnimation(p, x, y, z, animArgs._idx, true, false);
                    break;
                case (ushort)AnimCommandCode.Delete + 100 * (ushort)AnimCommandMode.DefaultCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, true, true);
                    break;

                // "/anim delete block [block]"
                case (ushort)AnimCommandCode.DeleteBlock + 100 * (ushort)AnimCommandMode.Default:
                    DeleteAnimation(p, x, y, z, animArgs._idx, false, true);
                    break;
                case (ushort)AnimCommandCode.DeleteBlock + 100 * (ushort)AnimCommandMode.DefaultCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, false, true);
                    break;

                // "/anim delete [num]"
                case (ushort)AnimCommandCode.DeleteNum + 100 * (ushort)AnimCommandMode.Default:
                    DeleteAnimation(p, x, y, z, animArgs._idx, false, false);
                    break;
                case (ushort)AnimCommandCode.DeleteNum + 100 * (ushort)AnimCommandMode.DefaultCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, false, true);
                    break;

                // "/anim swap [num 1] [num 2]"
                case (ushort)AnimCommandCode.SwapNum1Num2 + 100 * (ushort)AnimCommandMode.Default:
                    SwapAnimation(p, x, y, z, animArgs);
                    break;
                case (ushort)AnimCommandCode.SwapNum1Num2 + 100 * (ushort)AnimCommandMode.DefaultCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, true, true);
                    break;

                // "/anim copy"
                case (ushort)AnimCommandCode.Copy + 100 * (ushort)AnimCommandMode.Default:
                    CopyAnimation(p, x, y, z, x2, y2, z2);
                    break;

                // "/anim cut"
                case (ushort)AnimCommandCode.Cut + 100 * (ushort)AnimCommandMode.Default:
                    CopyAnimation(p, x, y, z, x2, y2, z2);
                    animArgs._commandMode = (ushort)AnimCommandMode.DefaultCuboid;
                    CuboidCommand((ushort)AnimCommandCode.Delete, p, animArgs, block, x, y, z, x2, y2, z2, true, false);
                    break;

                // "/anim paste"
                case (ushort)AnimCommandCode.Paste + 100 * (ushort)AnimCommandMode.Default:
                    PasteAnimation(p, x, y, z, 0, true, true);
                    break;
                case (ushort)AnimCommandCode.Paste + 100 * (ushort)AnimCommandMode.Append:
                    PasteAnimation(p, x, y, z, 0, false, true);
                    break;
                case (ushort)AnimCommandCode.Paste + 100 * (ushort)AnimCommandMode.Prepend:
                    PasteAnimation(p, x, y, z, 0, false, false);
                    break;

                // "/anim reverse"
                case (ushort)AnimCommandCode.Reverse + 100 * (ushort)AnimCommandMode.Default:
                    ReverseAnimation(p, x, y, z);
                    break;
                case (ushort)AnimCommandCode.Reverse + 100 * (ushort)AnimCommandMode.DefaultCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, true, false);
                    break;

                // "/anim shift"
                case (ushort)AnimCommandCode.Shift + 100 * (ushort)AnimCommandMode.Default:
                    ShiftAnimation(p, x, y, z, animArgs._startTick);
                    break;
                case (ushort)AnimCommandCode.Shift + 100 * (ushort)AnimCommandMode.DefaultCuboid:
                    CuboidCommand(animArgs._commandCode, p, animArgs, block, x, y, z, x2, y2, z2, true, false);
                    break;

                // "/anim paste delay"
                case (ushort)AnimCommandCode.PasteDelay + 100 * (ushort)AnimCommandMode.Default:
                    PasteAnimation(p, x, y, z, animArgs._startTick, true, true);
                    break;
                case (ushort)AnimCommandCode.PasteDelay + 100 * (ushort)AnimCommandMode.Append:
                    PasteAnimation(p, x, y, z, animArgs._startTick, false, true);
                    break;
                case (ushort)AnimCommandCode.PasteDelay + 100 * (ushort)AnimCommandMode.Prepend:
                    PasteAnimation(p, x, y, z, animArgs._startTick, false, false);
                    break;

                // "/anim info"
                case (ushort)AnimCommandCode.Info + 100 * (ushort)AnimCommandMode.Default:
                    InfoAnim(p, x, y, z);
                    break;
                default:
                    break;
            }

            AnimationHandler.SendCurrentFrame(p.level);
            return true;
        }

        // Places a block list for the list commands e.g., "/anim [start] [block 1] [duration 1] [block 2] [duration 2]..."
        private void PlaceBlockList(Player p, ushort x, ushort y, ushort z, short start, List<ushort> blockList, List<ushort> durationList, bool all, bool append)
        {
            if (all)
            {
                AnimationHandler.Delete(p.level, x, y, z, 1, true, false);
            }

            ushort index = 1;

            ushort interval = 0;
            foreach (ushort duration in durationList)
            {
                interval += duration;
            }

            for (int i = 0; i < blockList.Count; i++)
            {
                AnimationHandler.Place(p.level, x, y, z, index, interval, durationList[i], start, ushort.MaxValue, blockList[i], false, append);

                start += (short)durationList[i];
                index += 1;
            }
        }

        // Compact way to handle essentially the same command across placing, deleting and swapping
        private void CuboidCommand(ushort commandCode, Player p, AnimationArgs animArgs, BlockID block, ushort x1, ushort y1, ushort z1, ushort x2, ushort y2, ushort z2, bool all, bool append)
        {
            for (ushort x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x += 1)
            {
                for (ushort y = Math.Min(y1, y2); y <= Math.Max(y1, y2); y += 1)
                {
                    for (ushort z = Math.Min(z1, z2); z <= Math.Max(z1, z2); z += 1)
                    {
                        switch (commandCode)
                        {
                            case (ushort)AnimCommandCode.BlockList:
                                PlaceBlockList(p, x, y, z, animArgs._startTick, animArgs._blockList, animArgs._durationList, all, append);
                                break;
                            case (ushort)AnimCommandCode.IntervalDuration:
                                PlaceAnimation(p, x, y, z, animArgs, block, all, append);
                                break;
                            case (ushort)AnimCommandCode.StartIntervalDuration:
                                PlaceAnimation(p, x, y, z, animArgs, block, all, append);
                                break;
                            case (ushort)AnimCommandCode.AppendIntervalDuration:
                                PlaceAnimation(p, x, y, z, animArgs, block, all, append);
                                break;
                            case (ushort)AnimCommandCode.AppendStartIntervalDuration:
                                PlaceAnimation(p, x, y, z, animArgs, block, all, append);
                                break;
                            case (ushort)AnimCommandCode.Delete:
                                DeleteAnimation(p, x, y, z, animArgs._idx, all, false);
                                break;
                            case (ushort)AnimCommandCode.DeleteNum:
                                DeleteAnimation(p, x, y, z, animArgs._idx, all, false);
                                break;
                            case (ushort)AnimCommandCode.DeleteBlock:
                                DeleteAnimation(p, x, y, z, animArgs._idx, all, true);
                                break;
                            case (ushort)AnimCommandCode.SwapNum1Num2:
                                SwapAnimation(p, x, y, z, animArgs);
                                break;
                            case (ushort)AnimCommandCode.Reverse:
                                ReverseAnimation(p, x, y, z);
                                break;
                            case (ushort)AnimCommandCode.Shift:
                                ShiftAnimation(p, x, y, z, animArgs._startTick);
                                break;
                            default:
                                break;
                        }
                    }
                }
            }
        }

        // Shifts an animation block
        private void ShiftAnimation(Player p, ushort x, ushort y, ushort z, short delay)
        {
            if (!AnimationHandler.HasAnims(p.level))
            {
                return;
            }

            MapAnimation mapAnimation = AnimationHandler.dictActiveLevels[p.level.name];
            mapAnimation.bEditingLock = true;

            foreach (AnimBlock aBlock in mapAnimation._blocks)
            {
                if (aBlock._x == x && aBlock._y == y && aBlock._z == z)
                {
                    foreach (var kvp in aBlock._loopList)
                    {
                        kvp.Value._startTick += delay;
                    }
                }
            }
            mapAnimation.bEditingLock = false;
        }

        // Reverses an animation block
        private void ReverseAnimation(Player p, ushort x, ushort y, ushort z)
        {
            if (!AnimationHandler.HasAnims(p.level))
            {
                return;
            }

            MapAnimation mapAnimation = AnimationHandler.dictActiveLevels[p.level.name];

            foreach (AnimBlock aBlock in mapAnimation._blocks)
            {
                if (aBlock._x == x && aBlock._y == y && aBlock._z == z)
                {
                    // First reverse individual loops
                    foreach (AnimLoop loop in aBlock._loopList.Values)
                    {
                        loop._startTick += (short)(loop._interval - loop._duration);
                    }

                    // Then reverse the sequence of loops. Idea is basically to get the LCM of the loop lengths, consider
                    // The "overall" loop to be a loop of length LCM starting on min(tick) and repeating at min(tick+LCM
                    // Then just reflect the starting times in that range
                    // TODO: This could make weird changes if LCM gets too big... Doubt it will happen in a practical situation

                    // Get the LCM of the loop lengths
                    long[] loopLengths = new long[aBlock._loopList.Count];
                    int i = 0;
                    short minStartTick = short.MaxValue;
                    foreach (AnimLoop loop in aBlock._loopList.Values)
                    {
                        loopLengths[i] = loop._interval;
                        if (loop._startTick < minStartTick)
                        {
                            minStartTick = loop._startTick;
                        }
                        i += 1;
                    }

                    ushort loopsLCM = (ushort)LCM(loopLengths);

                    // Now start shuffling the inner loops inside the larger loop. You can shuffle them mod their own lengths, which I do here
                    foreach (AnimLoop loop in aBlock._loopList.Values)
                    {
                        loop._startTick = (short)((loop._startTick + loopsLCM - minStartTick) % loop._interval + minStartTick);
                    }
                }
            }
        }

        // Beautifully compact code I found on StackOverflow:
        long LCM(long[] numbers)
        {
            return numbers.Aggregate(LCM);
        }
        long LCM(long a, long b)
        {
            return Math.Abs(a * b) / GCD(a, b);
        }
        long GCD(long a, long b)
        {
            return b == 0 ? a : GCD(b, a % b);
        }

        // TODO: make this work with append and prepend
        // Pastes the player's selected animations
        private void PasteAnimation(Player p, ushort x, ushort y, ushort z, short delay, bool all, bool append)
        {
            if (!p.Extras.Contains("AnimCopy"))
            {
                p.Message("No available copy");
                return;
            }

            List<AnimBlock> CopyArray = (List<AnimBlock>)p.Extras["AnimCopy"];
            Vec3U16 CopyCoords = (Vec3U16)p.Extras["AnimCopyCoords"];
            ushort xBound = p.Level.Width; ushort yBound = p.level.Height; ushort zBound = p.level.Length;

            int Count = 0;
            foreach (AnimBlock aBlock in CopyArray)
            {
                Count += aBlock._loopList.Count;
            }

            if (CopyArray.Count + AnimationHandler.dictActiveLevels[p.level.name]._numLoops >= UInt16.MaxValue)
            {
                p.Message("Copy too large to paste - animation loop limit reached");
                return;
            }

            foreach (AnimBlock aBlock in CopyArray)
            {
                AnimBlock tmpCopy = new AnimBlock(aBlock._x, aBlock._y, aBlock._z, aBlock._currentBlock)
                {
                    _loopList = new SortedList<BlockID, AnimLoop>(aBlock._loopList)
                };

                // If "all" is true, we want to overwrite the animation. Must then delete existing loop first
                if (all)
                {
                    AnimationHandler.Delete(p.level, (ushort)(x + aBlock._x - CopyCoords.X), (ushort)(y + aBlock._y - CopyCoords.Y), (ushort)(z + aBlock._z - CopyCoords.Z), 1, true, false);
                }

                foreach (var kvp in tmpCopy._loopList)
                {
                    AnimLoop loop = kvp.Value;

                    if (x + aBlock._x - CopyCoords.X >= 0 && x + aBlock._x - CopyCoords.X < xBound
                        && y + aBlock._y - CopyCoords.Y >= 0 && y + aBlock._y - CopyCoords.Y < yBound
                        && z + aBlock._z - CopyCoords.Z >= 0 && z + aBlock._z - CopyCoords.Z < zBound)
                    {
                        AnimationHandler.Place(p.level, (ushort)(x + aBlock._x - CopyCoords.X), (ushort)(y + aBlock._y - CopyCoords.Y), (ushort)(z + aBlock._z - CopyCoords.Z), kvp.Key, loop._interval, loop._duration, (short)(loop._startTick + delay), loop._endTick, loop._block, false, append);
                    }
                }
            }
        }

        // Copies the player's selected animations
        private void CopyAnimation(Player p, ushort x1, ushort y1, ushort z1, ushort x2, ushort y2, ushort z2)
        {
            if (Math.Abs(x1 - x2 + 1) * Math.Abs(y1 - y2 + 1) * Math.Abs(z1 - z2 + 1) > 65535)
            {
                p.Message("Selection too large to copy");
                return;
            }

            if (!AnimationHandler.HasAnims(p.level))
            {
                p.Message("No animations to copy");
                return;
            }

            MapAnimation mapAnimation = AnimationHandler.dictActiveLevels[p.level.name];

            List<AnimBlock> copyArray = new List<AnimBlock>();

            for (int x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++)
            {
                for (int y = Math.Min(y1, y2); y <= Math.Max(y1, y2); y++)
                {
                    for (int z = Math.Min(z1, z2); z <= Math.Max(z1, z2); z++)
                    {
                        foreach (AnimBlock aBlock in mapAnimation._blocks)
                        {
                            if (aBlock._x == x && aBlock._y == y && aBlock._z == z)
                            {
                                AnimBlock Copy = new AnimBlock(aBlock._x, aBlock._y, aBlock._z, aBlock._currentBlock)
                                {
                                    _loopList = new SortedList<BlockID, AnimLoop>(aBlock._loopList)
                                };
                                copyArray.Add(Copy);
                            }
                        }
                    }
                }
            }

            p.Extras["AnimCopy"] = copyArray;
            p.Extras["AnimCopyCoords"] = new Vec3U16(x1, y1, z1);

            p.Message("Copied selection");
        }

        // Deletes an animation block at (x, y, z). If all is true, then we delete all animations at that position. Else delete that of a specific loop within an animation block with index idx
        private void DeleteAnimation(Player p, ushort x, ushort y, ushort z, ushort idx, bool all, bool deleteBlock)
        {
            AnimationHandler.Delete(p.level, x, y, z, idx, all, deleteBlock);

            // Also need to remove the blocks from view
            foreach (Player pl in p.level.players)
            {
                pl.RevertBlock(x, y, z);
            }
        }

        // Places an animation block at (x, y, z)
        private void PlaceAnimation(Player p, ushort x, ushort y, ushort z, AnimationArgs animArgs, BlockID block, bool all, bool append)
        {
            AnimationHandler.Place(p.level, x, y, z, animArgs._idx, animArgs._interval, animArgs._duration, animArgs._startTick, animArgs._endTick, block, all, append);
        }

        // Swaps two loops inside an index. IF a loop doesn't exist, it just changes the key in a straightforward fashion
        private void SwapAnimation(Player p, ushort x, ushort y, ushort z, AnimationArgs animArgs)
        {
            ushort idx1 = animArgs._idx;
            ushort idx2 = animArgs._idx2;

            if (!AnimationHandler.HasAnims(p.level)) return;

            if (idx1 == idx2) return;

            MapAnimation mapAnimation = AnimationHandler.dictActiveLevels[p.level.name];

            foreach (AnimBlock animBlock in mapAnimation._blocks)
            {
                if (animBlock._x == x && animBlock._y == y && animBlock._z == z)
                {
                    // (Set the 0 index to the smallest available index. Working my way around a previous bug involving zero indices)
                    if (idx1 == 0 || idx2 == 0)
                    {
                        ushort i = 1;
                        while (!animBlock._loopList.ContainsKey(i))
                        {
                            i += 1;
                        }
                        if (idx1 == 0)
                        {
                            idx1 = i;
                        }
                        else
                        {
                            idx2 = i;
                        }
                    }

                    if (!animBlock._loopList.ContainsKey(idx1) && !animBlock._loopList.ContainsKey(idx2))
                    {
                        return;
                    }
                    else if (!animBlock._loopList.ContainsKey(idx1))    // loopList[idx2] does exist
                    {
                        animBlock._loopList[idx1] = animBlock._loopList[idx2];
                        animBlock._loopList.Remove(idx2);
                    }
                    else if (!animBlock._loopList.ContainsKey(idx2))  // loopList[idx1] does exist
                    {
                        animBlock._loopList[idx2] = animBlock._loopList[idx1];
                        animBlock._loopList.Remove(idx1);
                    }
                    else
                    {
                        AnimLoop loop1 = animBlock._loopList[idx1];
                        AnimLoop tmp = new AnimLoop(loop1._interval, loop1._duration, loop1._startTick, loop1._endTick, loop1._block);
                        animBlock._loopList[idx1] = animBlock._loopList[idx2];
                        animBlock._loopList[idx2] = tmp;
                    }
                }
            }
        }

        /***************************
         * STOP, START, SAVE, INFO *
         ***************************/

        void StopAnim(Level level)
        {
            if (AnimationHandler.HasAnims(level))
            {
                AnimationHandler.dictActiveLevels[level.name].bRunning = false;
            }
            return;
        }

        void StartAnim(Level level)
        {
            if (AnimationHandler.HasAnims(level))
            {
                AnimationHandler.dictActiveLevels[level.name].bRunning = true;
                AnimationHandler.dictActiveLevels[level.name].bEditingLock = false;     // If this for some reason became "true" can turn it off here
            }
            return;
        }

        // Sends the information about a block to the player
        void InfoAnim(Player p, ushort x, ushort y, ushort z)
        {
            Level level = p.level;
            if (AnimationHandler.HasAnims(level))
            {
                MapAnimation mapAnimation = AnimationHandler.dictActiveLevels[level.name];

                AnimBlock selection = new AnimBlock(x, y, z, Block.Air)
                {
                    _loopList = null
                };

                foreach (AnimBlock animBlock in mapAnimation._blocks)
                {
                    if (animBlock._x == x && animBlock._y == y && animBlock._z == z)
                    {
                        selection = animBlock;
                        break;
                    }
                }

                if (selection._loopList == null)
                {
                    p.Message("No information to show.");
                    return;
                }

                // If you made it this far the animation block was found and has something to show
                p.Message("|index    |interval |duration |start    |end      |blockID  |");
                AnimLoop currentLoop;
                foreach (var kvp in selection._loopList)
                {
                    currentLoop = kvp.Value;
                    p.Message(String.Format("|{0}{1}|{2}{3}|{4}{5}|{6}{7}|{8}{9}|{10}{11}|",
                        kvp.Key, new String(' ', 8 - kvp.Key.ToString().Length),
                        currentLoop._interval, new String(' ', 8 - currentLoop._interval.ToString().Length),
                        currentLoop._duration, new String(' ', 8 - currentLoop._duration.ToString().Length),
                        currentLoop._startTick, new String(' ', 8 - currentLoop._startTick.ToString().Length),
                        currentLoop._endTick, new String(' ', 8 - currentLoop._endTick.ToString().Length),
                        Block.ToRaw(currentLoop._block), new String(' ', 8 - Block.ToRaw(currentLoop._block).ToString().Length)));
                }
            }
            return;
        }

        /***************
        * HELP METHODS *
        ****************/

        public override void Help(Player p)
        {
            Help(p, "");
        }

        public override void Help(Player p, string msg)
        {
            switch (msg)
            {
                case "6":
                    p.Message(@"%EUse append : a ");
                    p.Message(@"%EUse prepend : p");
                    p.Message(@"%EUse cuboid : z");
                    p.Message(@"%EE.g., /animation a z [start] [interval] [duration]");
                    p.Message(@"%EIf you see a question mark e.g., ""[z?]"" then that option is optional");
                    p.Message(@"%EAdd ""z"", ""append"" and/or ""prepend"" to choose the command mode");
                    p.Message(@"%EFor instance /anim a z [start] [block 1] [duration 1] [block 2] [duration 2]...");
                    break;
                case "5":
                    p.Message(@"%T/Animation restart");
                    p.Message(@"%ERestarts an animation");
                    p.Message(@"%T/Animation at [tick]");
                    p.Message(@"%EMoves the counter to the given tick");
                    p.Message(@"%EAnimation stop");
                    p.Message(@"%EPauses the animation");
                    p.Message(@"%EAnimation start");
                    p.Message(@"%EContinues your animation");
                    p.Message(@"%T/Animation info");
                    p.Message(@"%EGets the info about an animation block");
                    p.Message(@"%T/Animation save");
                    p.Message(@"%ESaves an animation");
                    p.Message(@"%EFor shortcuts, see /help animation 6");
                    p.Message(@"%T/Animation speed [ms]");
                    p.Message(@"%EChange animation speed. Default is 100=1/10th of a second");
                    p.Message(@"%T/Animation remove all");
                    p.Message(@"%EPermanently deletes all animations on the map");
                    p.Message(@"%T/Animation killer");
                    p.Message(@"%EAllows animated blocks to use killer blockprops (can be slower)");
                    p.Message(@"%T/Animation backup");
                    p.Message(@"%ECreates a backup of your animation. Can only have 1 backup at a time");
                    break;
                case "4":
                    p.Message(@"%T/Animation copy");
                    p.Message(@"%ECopies a cuboid of animation blocks");
                    p.Message(@"%T/Animation cut");
                    p.Message(@"%ECuts a cuboid of animation blocks");
                    p.Message(@"%T/Animation paste [a/p?] [delay?]");
                    p.Message(@"%EPastes your animations with an optional delay");
                    p.Message(@"%EFor miscellaneous animation commands, see /help animation 5");
                    break;
                case "3":
                    p.Message(@"%T/Animation delete [z?] [num]");
                    p.Message(@"%EDeletes animation loops with the given index");
                    p.Message(@"%T/Animation delete [z?] block [block]");
                    p.Message(@"%EDeletes all loops with the given block in it");
                    p.Message(@"%T/Animation swap [z?] [num 1] [num 2]");
                    p.Message(@"%ESwap two loops in an animation block (by index)");
                    p.Message(@"%EFor copying and pasting animations, see /help animation 4");
                    break;
                case "2":
                    p.Message(@"%T/Animation [a/p?] [z?] [start?] [interval] [duration]");
                    p.Message(@"%ECreate an animation that start on a given tick");
                    p.Message(@"%T/Animation delete");
                    p.Message(@"%EDelete an animation block");
                    p.Message(@"%T/Animation [z?] shift [delay]");
                    p.Message(@"%EShifts an animation");
                    p.Message(@"%T/Animation reverse [z?]");
                    p.Message(@"%EReverses an animation");
                    p.Message(@"%EFor appending/prepending new loops in front of/behind existing animation blocks, see /help animation 3");
                    break;
                case "0":
                    p.Message(@"%EAnimations let us create blocks that periodically toggle on and off");
                    p.Message(@"%EWhen a map is loaded it begins a timer that starts at 0 and ticks forward every 10th of a second");
                    p.Message(@"%EThe animation command uses...");
                    p.Message(@"%E(1) [start] to indicate which tick to start on");
                    p.Message(@"%E(2) [interval] to indicate the period of the animation");
                    p.Message(@"%E(3) [duration] to indicate the length of time the block will be visible");
                    p.Message(@"%EAnimations are such loops that are put into animation blocks");
                    p.Message(@"%EAnimation blocks can contain several loops at once. By default we overwrite all loops");
                    p.Message(@"%EFor an animation block with several loops, we render ones with higher indices in front of ones with lower indices");
                    p.Message(@"%EWhen these loops are in their ""off"" state, they render the normal block behind them");
                    p.Message(@"%EThe ""append/prepend"" (a/p) flag gives us more control over how to manipulate loops in the same animation block");
                    p.Message(@"%EThey tell us whether to place a loop in front of or behind eisting loops");
                    p.Message(@"%EAdd ""z"", ""append"" and/or ""prepend"" to choose the command mode");
                    p.Message(@"%EFor instance /anim append z [start] [block 1] [duration 1] [block 2] [duration 2]...");
                    break;
                default:
                    p.Message(@"%EFor a complete explanation use /help animation 0");
                    p.Message(@"%T/Animation [a/p?] [z?] [start] [block 1] [duration 1] [block 2] [duration 2]...");
                    p.Message(@"%ECreates an animation blocks that starts on [start] and repeats the sequence indefinitely");
                    p.Message(@"%EFor information on how to manipulate loops see /help animation 2");
                    break;
            }
        }
    }
}