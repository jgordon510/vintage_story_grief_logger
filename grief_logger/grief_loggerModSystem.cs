using Vintagestory.API.Common;
using System.IO;
using System;
using Vintagestory.API.MathTools;
using System.Collections.Generic;
using Vintagestory.API.Server;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Diagnostics;
using Vintagestory.API.Util;
using System.Collections;
using static System.Net.Mime.MediaTypeNames;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using static System.Formats.Asn1.AsnWriter;
using Vintagestory.API.Datastructures;
using System.Linq.Expressions;
using System.Xml.Linq;
using ProperVersion;
using System.Reflection.PortableExecutable;
using System.Text;

namespace grieflogger
{

    /*
    This mod works by storing information about placed and broken/used blocks in the savegame file.  
    When a user first places a block in a chunk, it marks the chunk with a unique id, and used that 
    id to key the placed block information in the file. Then, when a user breaks a block, it checks 
    for the presence of that id, and if it finds it, compares the block being broken to the blocks 
    in that chunk's placed block information.  If it finds a matching block that was placed by a 
    different player, it marks that in a single table. You can output that table as a csv file by 
    typing /grieflog.
     */
    public class griefloggerModSystem : ModSystem
    {
        ConfigSettings config;
        const bool DEBUG = false;               //will disable owner mismatch and enable logging
        
        public ICoreAPI api;
        private ICoreServerAPI sapi;
        private int chatGroup;
        private string docPath;
        //brokenData is a simple string list of delimited values
        List<string> brokenData;                        
        //placed data is a dictionary of similar string lists organized by chunk id
        Dictionary<string, List<string>>  placedData;
        StreamWriter outputFile;
        public override void Start(ICoreAPI api)
        {
            this.api = api;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            this.sapi = api;
            //load or make config settings
            config = sapi.LoadModConfig<ConfigSettings>("grief_log.json");
            
            if (config == null)
            {
                config = new ConfigSettings();
                sapi.StoreModConfig(config, "grief_log.json");
            }
            string filePath = Environment.SpecialFolder.ApplicationData.ToString();
            docPath = Path.GetDirectoryName(filePath);
            //outputFile = new StreamWriter(Path.Combine(docPath, "grief-log.csv"), true);
            
            SetChatGroup();
            //these methods deal with griefing
            sapi.Event.BreakBlock += Event_BreakBlock; 
            sapi.Event.DidUseBlock += Event_DidUseBlock;
            sapi.Event.OnEntityDeath += OnEntityDeath;
            //this method deals with placement
            sapi.Event.DidPlaceBlock += Event_DidPlaceBlock;
           
            //broken data is loaded entirely with a nullcheck
            brokenData = GetData("brokenData") ?? new List<string>();
            //placed data is loaded by chunk as needed so it's just a blank dict for now
            placedData = new Dictionary<string, List<string>>();
            //this command allows the outputting of the grieflog
            sapi.ChatCommands.Create("grieflog")
                .WithDescription("Dump the broken log")
                .RequiresPrivilege(Privilege.kick)
                //.WithArgs(api.ChatCommands.Parsers.Word("cmd", new string[] { "list", "join", "leave" }))
                .HandleWith(new OnCommandDelegate(OnLfg));
        }
        private void SetChatGroup()
        {
            switch (config.CHAT_GROUP)
            {
                case "AllChatGroups":
                    chatGroup = GlobalConstants.AllChatGroups;
                    break;
                case "ConsoleGroup":
                    chatGroup = GlobalConstants.ConsoleGroup;
                    break;
                case "DamageLogChatGroup":
                    chatGroup = GlobalConstants.DamageLogChatGroup;
                    break;
                case "GeneralChatGroup":
                    chatGroup = GlobalConstants.GeneralChatGroup;
                    break;
                case "ServerInfoChatGroup":
                    chatGroup = GlobalConstants.ServerInfoChatGroup;
                    break;
                default:
                    chatGroup = GlobalConstants.InfoLogChatGroup;
                    break;

            }
        }
        /*
         retrieves a stringlist by key from the savegame file
         */
        private List<string> GetData(string key)
        {
            byte[] data = sapi.WorldManager.SaveGame.GetData(key);
            if (data == null) return null;
            return SerializerUtil.Deserialize<List<string>>(data);
        }

        /*
         processed the /grieflist command from chat
         */
        private TextCommandResult OnLfg(TextCommandCallingArgs args)
        {
            //OutputLog(brokenData, "grief_log.csv");
            return TextCommandResult.Success("Dumped the log successfully!");
        }

        /*
        further deserializes the string by splitting it by its delimitter
        I probably should have stored a dictionary but I couldn't figure out the deserializer at the time
        and thought it only supported flat string lists.
        I'm going to leave it.  It matches the output of the log files and seems like it's probaly as efficient
        in terms of space and performance.
         */
        private List<string> ParseLine(string line)
        {
            return new List<string>(line.Split(';'));
        }

        /*
        performs the query function to determine if a block has been griefed
        it iterates the chunk's placed blocks checking to see if they match in location and don't
        match by owner.  If those locations are match, it returns true.
        If I had used a dictionary, it would use contains in place of this, and I'd key by the locString.
         */
        private List<string> CheckForPlacedBlock(string locString, string uid, string chunkID)
        {
            placedData[chunkID] = GetData("placedData" + chunkID) ?? new List<string>();
            foreach (var line in placedData[chunkID])
            {
                List<string> parsed = ParseLine(line);
                //matches location but not uid
                bool locMatch = parsed[1] == locString;
                //api.Logger.Debug("Comparing: {0}:{1}", parsed[1], locString);
                bool ownerMisMatch = parsed[2] != uid;
                if (DEBUG) ownerMisMatch = true;
                if (locMatch && ownerMisMatch) return parsed;
            }
            return null;
        }
        /*
         this outputs the actual log as a csv into the application folder.  opens nicely in google sheets.
         */
        private void AddLogLine(string line)
        {
            if (sapi?.Side == null) return;
            if (sapi.Side.IsClient()) return;
            try
            {
                using (outputFile = new StreamWriter(Path.Combine(docPath, "grief-log.csv"), true))
                {
                    outputFile.WriteLine(line);
                }
            }
            catch (Exception e)
            {
                sapi.Logger.Debug("Couldn't write to file...");
            }
            return;
        }
        private void OutputLog(List<string> dataStore, string fn)
        {
            string filePath = Environment.SpecialFolder.ApplicationData.ToString();
            //api.Logger.Debug(filePath);
            
            bool append = false;

            using (StreamWriter outputFile = new StreamWriter(Path.Combine(docPath, fn), append))
            {
                string header = "TIMESTAMP;LOCATION;BREAKER ID;BREAKER NAME;BLOCK ID;BLOCK CODE;OWNER TIMESTAMP;OWNER ID;OWNER NAME;MAP COORDS";
                outputFile.WriteLine(header);
                foreach (string s in dataStore)
                {
                    outputFile.WriteLine(s);
                }
            }
            return;
        }

        /*
         sends an announcement to the general chat of all players
         */
        private void Announce(string announcement)
        {
            foreach (var plr in sapi.World.AllOnlinePlayers)
            {
                var p = plr as IServerPlayer;
                if (p.Entity != null && p.ConnectionState == EnumClientState.Playing)
                {
                    sapi.Logger.Debug(plr.Role.Code.ToString());
                    if (config.CHAT_ROLES.Contains(plr.Role.Code.ToString()))
                    {
                        p.SendMessage(chatGroup, announcement, EnumChatType.Notification);
                    }
                }
                else
                {
                    if (DEBUG) sapi.Logger.Debug("I didn't send {0} to a player", announcement);
                }

            }
           
        }
        private string GetMapCoords(BlockPos pos)
        {
            EntityPos spawn = sapi.World.DefaultSpawnPosition;
            string mapCoords = Convert.ToInt32(pos.X - spawn.X).ToString() + " ";
            mapCoords += Convert.ToInt32(pos.Y).ToString() + " ";
            mapCoords += Convert.ToInt32(pos.Z - spawn.Z).ToString();
            return mapCoords;
        }
        /*
        generates a grieflog string as a delimimted set of text fields
        it also makes the announcement to the chat for griefed blocks and used items
        DOES NOT handle poaching of animals
         */
        private string GetLogString(string locString, IServerPlayer byPlayer, BlockSelection blockSel, List<string> oldData, bool isBomb, string bombedCode)
        {
            BlockPos pos = blockSel.Position;
            string mapCoords = GetMapCoords(pos);

            string id = "";
            string code = "";
            if(!isBomb)
            {
                if (blockSel.Block == null) return null;
                if (blockSel.Block.Code == null) return null;
                id = blockSel.Block.Id.ToString();
                code = blockSel.Block.Code.ToString();
            } else
            {
                id = "BOMB_PLACEMENT";
                code = bombedCode;
            }

            if (!config.CHAT_CODE_WHITELIST.Contains(code))
            {
                string announcement = string.Format("{0} just griefed {1}'s {2} at {3}", byPlayer.PlayerName, oldData[3], code, mapCoords);
                if (isBomb) announcement = string.Format("{0} just placed a bomb near {1}'s {2} at {3}", byPlayer.PlayerName, oldData[3], code, mapCoords);
                Announce(announcement);
                
            }
            string s = DateTime.Now.ToString();         //0
            s += ";" + locString;                       //1 
            s += ";" + byPlayer.PlayerUID;              //2
            s += ";" + byPlayer.PlayerName;             //3
            s += ";" + id;                              //4
            s += ";" + code;                            //5
            s += ";" + oldData[0];                      //6 original date
            s += ";" + oldData[2];                      //7 original player id
            s += ";" + oldData[3];                      //8 original player name
            s += ";" + mapCoords + ";";                 //9 map coordinates of block
            return s;
        }

        /*
         determines whether a pos is in a claim
        uses the entity to get world because api was causing errors
         */
        private bool InClaim(BlockPos pos, EntityPlayer entity)
        {
            try
            {
                LandClaim[] claims = entity.World.Claims.Get(pos);
                return claims.Length > 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("GRIEFLOG (claim error): " + e.Message);
                return false;
            }
            
        }

        /*
         checks wether a player is griefing a particular position by using or breaking
         */

        private List<string> isGriefed(BlockPos pos, IServerPlayer byPlayer)
        {
            IWorldChunk chunk = sapi.World.BlockAccessor.GetChunkAtBlockPos(pos);
            string chunkID = chunk.GetModdata<string>(config.GRIEF_KEY);
            if (chunkID == null) return null;
            string locString = pos.ToString();
            List<string> oldData = CheckForPlacedBlock(locString, byPlayer.PlayerUID, chunkID);
            return oldData;
        }

        /*
         commits a string to the grief_log.csv file
         *//*
        private void AddLogLine(string s)
        {
            brokenData.Add(s);
            while (brokenData.Count > config.BROKEN_LINE_LIMIT)
            {
                brokenData.PopAt(0); //right?
            }
            sapi.WorldManager.SaveGame.StoreData("brokenData", SerializerUtil.Serialize(brokenData));

            if (DEBUG) sapi.Logger.Debug(s);
        }*/

        /*
        manages the registering of grief events
        nullcheck guard clause on the chunk id
        uses CheckForPlacedBlock to create oldata, again with a nullcheck guard clause
        finally, if it makes it, it gets the log string and saves it to the grieflog
         */
        private void PossibleGriefEvent(IServerPlayer byPlayer, BlockSelection blockSel)
        {
            BlockPos pos = blockSel.Position;
            if (InClaim(pos, byPlayer.Entity)) {
                api.Logger.Debug("IN a claim, not a grief.");
                return; 
            }
            //api.Logger.Debug("not in claim");
            List<string> oldData = isGriefed(pos, byPlayer);
            if(oldData == null) return;
            // api.Logger.Debug("old data found");
            string locString = pos.ToString();
            string s = GetLogString(locString, byPlayer, blockSel, oldData, false, "");
            if (s == null) return;
            AddLogLine(s);
            // api.Logger.Debug("committing log line...");
        }

        /*
        manages the registering of grief bomb placement events
        this must iterate through all blocks in range of the blastradius
        if it find a griefable blocks it makes the annoucement for that block and stops
         */
        private bool PossibleGriefBombEvent(IServerPlayer byPlayer,  BlockSelection blockSel, BlockPos oldPos)
        {
            Block oldBlock = sapi.World.BlockAccessor.GetBlock(oldPos);
            if (InClaim(oldPos, byPlayer.Entity)) return false;
            List<string> oldData = isGriefed(oldPos, byPlayer);
            if (oldData == null) return false;
            string locString = oldPos.ToString();
            Block broken = sapi.World.BlockAccessor.GetBlock(oldPos);
            string s = GetLogString(locString, byPlayer, blockSel, oldData, true, broken.Code.ToString());
            if (s == null) return false;
            AddLogLine(s);
            return true;
        }

        //overrides for the possible grief events
        private void Event_BreakBlock(IServerPlayer byPlayer, BlockSelection blockSel, ref float dropQuantityMultiplier, ref EnumHandling handling)
        {
            if (byPlayer == null) return;
            if (blockSel == null) return;
            try
            {
                PossibleGriefEvent(byPlayer, blockSel);
            }
            catch (Exception e)
            {
                api.Logger.Debug("GRIEFLOGGER EXCEPTION: {0}",e.Message);
            }
        }
        private void Event_DidUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
        {
            if (byPlayer == null) return;
            if (blockSel == null) return;
            try
            {
                PossibleGriefEvent(byPlayer, blockSel);
            }
            catch (Exception e)
            {
                sapi.Logger.Debug("GRIEFLOGGER EXCEPTION: {0}", e.Message);
            }
        }

        /*
         checks for an existing chunk id and creates one if there isn't already
        creates the internal log line and stores it in the chunk's placeData on the save
         */
        private void Event_DidPlaceBlock(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel, ItemStack withItemStack)
        {
            if(blockSel== null) return;
            BlockPos pos = blockSel.Position;
            //check if a bomb is being placed to grief; bombs are not griefable so we're done if we find bomb griefing
            if (CheckBomb(byPlayer, pos, blockSel)) return; 
            IWorldChunk chunk = sapi.World.BlockAccessor.GetChunkAtBlockPos(pos);
            string chunkID = chunk.GetModdata<string>(config.GRIEF_KEY);
            if (chunkID == null)
            {
                Guid guid = Guid.NewGuid();
                chunkID = guid.ToString();
                if (DEBUG) sapi.Logger.Debug("Generated chunk id: {0}", chunkID);
                chunk.SetModdata<string>(config.GRIEF_KEY, chunkID);
                chunk.MarkModified();
            }

            placedData[chunkID] = GetData("placedData" + chunkID) ?? new List<string>();
            string s = DateTime.Now.ToString();         //0
            s += ";" + pos.ToString();                  //1
            s += ";" + byPlayer.PlayerUID;              //2
            s += ";" + byPlayer.PlayerName + ";";       //3
            placedData[chunkID].Add(s);
            sapi.WorldManager.SaveGame.StoreData("placedData" + chunkID, SerializerUtil.Serialize(placedData[chunkID]));
        }

        /*
         checks placed object to see if it's a bomb
        if so, iterates to find blocks in the blast radius
        upon finding a griefable block, announces and logs then returns
        only reports a single block
         */
        private bool CheckBomb(IServerPlayer byPlayer, BlockPos pos, BlockSelection blockSel) {
            if(blockSel==null) return false;
            Block block = sapi.World.BlockAccessor.GetBlock(pos);
            FastVec3d posVec = new(pos.X, pos.Y, pos.Z);
            int blastRadius = 4;
            string bombCode = "game:oreblastingbomb";
            if (bombCode != block.Code.ToString()) return false;
            for(int x = pos.X-blastRadius; x <= pos.X + blastRadius; x++)
            {
                for (int y = pos.Y - blastRadius; y <= pos.Y + blastRadius; y++)
                {
                    for(int z = pos.Z - blastRadius; z <= pos.Z + blastRadius; z++)
                    {
                        FastVec3d oldPosVec = new(x, y, z);
                        double dist = oldPosVec.Distance(posVec);
                        if(dist <= blastRadius)
                        {
                            if (x == pos.X && y == pos.Y && z == pos.Z) continue;
                            BlockPos oldPos = new(x, y, z);
                            if(PossibleGriefBombEvent(byPlayer, blockSel, oldPos)) return true;   
                        }
                    }
                }
            }
            return true;
        }
        
        /*
        this deals with animal poaching
        only logs if in someone else's claim AND
        the animal is generation 1+
         */
        private void OnEntityDeath(Entity entity, DamageSource damageSource)
        {
            //use /entity cmd l[] setgen 3 to set generation
            LandClaim[] lc = null;
            try
            {
                lc = entity.World.Claims.Get(entity.Pos.AsBlockPos);
                if (lc == null)
                {
                    return; //End function if dead creature isn't on a claim. We'll only be tracking creatures killed within a claim.
                }
            } catch(Exception e)
            {
                sapi.Logger.Debug("GRIEFLOGGER EXCEPTION: {0}", e.Message);
                return;
            }
            
            if (!entity.IsCreature) return;
            string killed = entity.GetName();
            killed = killed.Remove(0, 5); // take off "dead "
            DateTime date = System.DateTime.Now;
            string killedByName = null;
            BlockPos pos = entity.Pos.AsBlockPos;
            if (damageSource != null)
            {
                if (damageSource.SourceEntity == null)
                {
                    killedByName = null;
                }
                else
                {
                    if (damageSource.SourceEntity is not EntityPlayer) return;
                    killedByName = damageSource.SourceEntity.GetName();
                }
            }
            string claimOwner = null;
            if (lc != null && lc.Length > 0)
            {
                claimOwner = lc[0].LastKnownOwnerName;
            }
            else return;
            int generation = entity.WatchedAttributes.GetInt("generation");
            if( generation == 0) return;
            if (!(claimOwner.Contains(killedByName)) || DEBUG)
            {
                EntityPos spawn = sapi.World.DefaultSpawnPosition;
                string mapCoords = GetMapCoords(pos);
                string announcement = string.Format("{0} just killed {1}'s {2} at {3}", killedByName, claimOwner, killed, mapCoords);
                Announce(announcement);

                /*
                 this is different enough for poaching that it makes sense to do it here instead of get logLine
                 */
                string s = date.ToString();                         //0 date
                s += ";" + pos.ToString();                          //1 position
                s += ";" + damageSource.SourceEntity.EntityId;      //2 killer id
                s += ";" + killedByName;                            //3 killer name
                s += ";" + "KILLED_ANIMAL";                         //4 "KILLED_ANIMAL"
                s += ";" + killed;                                  //5 animal killed
                s += ";" + "GEN: " + generation.ToString();         //6 AGE?
                s += ";" + lc[0].OwnedByPlayerUid;                  //7 claim owner id
                s += ";" + claimOwner;                              //8 claim owner name
                s += ";" + mapCoords + ";";                         //9 map coordinates of animal 
                if (s == null) return;
                AddLogLine(s);
            }
        }
    }

    

    /*
     C# has no pop on a list, so I added this to trim the grieflog
     */
    static class ListExtension
    {
        public static T PopAt<T>(this List<T> list, int index)
        {
            T r = list[index];
            list.RemoveAt(index);
            return r;
        }
    }
}
