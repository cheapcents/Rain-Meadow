﻿using MonoMod.Cil;
using System.Linq;
using System;
using Mono.Cecil.Cil;
using Steamworks;

namespace RainMeadow
{
    partial class RainMeadow
    {
        private void GameHooks()
        {
            On.WorldLoader.ctor_RainWorldGame_Name_bool_string_Region_SetupValues += WorldLoader_ctor_RainWorldGame_Name_bool_string_Region_SetupValues;
            On.WorldLoader.ctor_RainWorldGame_Name_bool_string_Region_SetupValues_LoadingContext += WorldLoader_ctor_RainWorldGame_Name_bool_string_Region_SetupValues_LoadingContext;
            On.WorldLoader.Update += WorldLoader_Update;
            On.RoomPreparer.ctor += RoomPreparer_ctor;
            On.RoomPreparer.Update += RoomPreparer_Update;
            On.AbstractRoom.Abstractize += AbstractRoom_Abstractize;

            On.Room.ctor += Room_ctor;
            IL.RainWorldGame.ctor += RainWorldGame_ctor;
            IL.Room.LoadFromDataString += Room_LoadFromDataString;
            IL.Room.Loaded += Room_Loaded;
        }

        // Room unload
        private void AbstractRoom_Abstractize(On.AbstractRoom.orig_Abstractize orig, AbstractRoom self)
        {
            if (self.world?.game?.session is OnlineGameSession os)
            {
                if (OnlineManager.lobby.worldSessions[self.world.region.name] is WorldSession ws && ws.roomSessions[self.name] is RoomSession rs)
                {
                    if (rs.isAvailable)
                    {
                        rs.abstractOnDeactivate = true;
                        rs.ReleaseResource();
                        return;
                    }
                }
            }
            orig(self);
        }

        // Room wait and activate
        private void RoomPreparer_Update(On.RoomPreparer.orig_Update orig, RoomPreparer self)
        {
            if (!self.shortcutsOnly && self.room?.game?.session is OnlineGameSession os)
            {
                if(OnlineManager.lobby.worldSessions[self.room.world.region.name].roomSessions[self.room.abstractRoom.name] is RoomSession rs)
                {
                    if (true) // force load scenario ????
                    {
                        SteamAPI.RunCallbacks();
                        OnlineManager.instance.RawUpdate(0.001f);
                    }
                    if (!rs.isAvailable)return;
                }
            }
            orig(self);
            if (!self.shortcutsOnly && self.room?.game?.session is OnlineGameSession)
            {
                if (OnlineManager.lobby.worldSessions[self.room.world.region.name].roomSessions[self.room.abstractRoom.name] is RoomSession rs)
                {
                    if (self.done) rs.Activate();
                }
            }
        }

        // room request
        private void RoomPreparer_ctor(On.RoomPreparer.orig_ctor orig, RoomPreparer self, Room room, bool loadAiHeatMaps, bool falseBake, bool shortcutsOnly)
        {
            if (!shortcutsOnly && room?.game?.session is OnlineGameSession os)
            {
                OnlineManager.lobby.worldSessions[room.world.region.name].roomSessions[room.abstractRoom.name].Request();
            }
            orig(self, room, loadAiHeatMaps, falseBake, shortcutsOnly);
        }

        // world wait
        private void WorldLoader_Update(On.WorldLoader.orig_Update orig, WorldLoader self)
        {
            if(self.game?.session is OnlineGameSession os)
            {
                if(self.game.overWorld?.worldLoader != self) // force-load scenario
                {
                    SteamAPI.RunCallbacks();
                    OnlineManager.instance.RawUpdate(0.001f);
                }
                if(!OnlineManager.lobby.worldSessions[self.world.region.name].isAvailable)
                {
                    if (UnityEngine.Input.anyKey) RainMeadow.Debug("skip 1");
                    return;
                }
                if(self.game.overWorld?.activeWorld is World aw && OnlineManager.lobby.worldSessions[aw.region.name].isAvailable)
                {
                    if (UnityEngine.Input.anyKey) RainMeadow.Debug("skip 1");
                    return;
                }
            }
            orig(self);
            if (self.game?.session is OnlineGameSession)
            {
                if (OnlineManager.lobby.worldSessions[self.world.region.name] is WorldSession ws)
                {
                    if (self.Finished) ws.Activate();
                }
            }
        }

        private void Room_ctor(On.Room.orig_ctor orig, Room self, RainWorldGame game, World world, AbstractRoom abstractRoom)
        {
            orig(self, game, world, abstractRoom);
            if (game != null && game.session is OnlineGameSession os)
            {
                os.FilterItems(self);
            }
        }

        private void RainWorldGame_ctor(ILContext il)
        {
            try
            {
                // part 1 - create right session type
                //else
                //{
                //    this.session = new StoryGameSession(manager.rainWorld.progression.PlayingAsSlugcat, this);
                //}
                // ========== becomes ===========
                //else if (self.manager.menuSetup.startGameCondition == OnlineGameSession.Ext_OnlineSession.Online)
                //{
                //    this.session = new OnlineGameSession(manager.rainWorld.progression.PlayingAsSlugcat, this);
                //}
                //else
                //{
                //    this.session = new StoryGameSession(manager.rainWorld.progression.PlayingAsSlugcat, this);
                //}
                var c = new MonoMod.Cil.ILCursor(il);
                c.GotoNext(moveType: MoveType.After,
                    i => i.MatchLdarg(0),
                    i => i.MatchLdarg(1),
                    i => i.MatchLdfld<ProcessManager>("rainWorld"),
                    i => i.MatchLdfld<RainWorld>("progression"),
                    i => i.MatchCallvirt<PlayerProgression>("get_PlayingAsSlugcat"),
                    i => i.MatchLdarg(0),
                    i => i.MatchNewobj<StoryGameSession>(),
                    i => i.MatchStfld<RainWorldGame>("session")
                    );
                var skip = c.IncomingLabels.Last();

                c.GotoPrev(i => i.MatchBr(skip));
                c.Index++;
                // we're right before story block here hopefully
                c.MoveAfterLabels();

                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate((RainWorldGame self) => { return self.manager.menuSetup.startGameCondition == Ext_StoryGameInitCondition.Online; });
                ILLabel story = il.DefineLabel();
                c.Emit(OpCodes.Brfalse, story);
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Newobj, typeof(OnlineGameSession).GetConstructor(new Type[] { typeof(RainWorldGame) }));
                c.Emit<RainWorldGame>(OpCodes.Stfld, "session");
                c.Emit(OpCodes.Br, skip);
                c.MarkLabel(story);

                //// part 2 - no breakie if no player/creatures
                //c.GotoNext(moveType: MoveType.After,
                //    i => i.MatchLdfld<AbstractRoom>("creatures"),
                //    i => i.MatchLdcI4(0),
                //    i => i.MatchCallOrCallvirt(out _),
                //    i => i.MatchStfld<RoomCamera>("followAbstractCreature")
                //    );

                //skip = c.IncomingLabels.Last();
                //c.GotoPrev(MoveType.After, i => i.MatchBrtrue(skip));
                //c.Emit(OpCodes.Br, skip); // don't run desperate bit of code that follows creatures[0]
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        private void WorldLoader_ctor_RainWorldGame_Name_bool_string_Region_SetupValues_LoadingContext(On.WorldLoader.orig_ctor_RainWorldGame_Name_bool_string_Region_SetupValues_LoadingContext orig, WorldLoader self, RainWorldGame game, SlugcatStats.Name playerCharacter, bool singleRoomWorld, string worldName, Region region, RainWorldGame.SetupValues setupValues, WorldLoader.LoadingContext context)
        {
            if (game?.session is OnlineGameSession os)
            {
                setupValues.worldCreaturesSpawn = os.ShouldLoadCreatures(game);
            }
            orig(self, game, playerCharacter, singleRoomWorld, worldName, region, setupValues, context);
            if (game?.session is OnlineGameSession )
            {
                if (game.overWorld?.activeWorld is World aw && OnlineManager.lobby.worldSessions[aw.region.name] is WorldSession ws)
                {
                    RainMeadow.Debug("Releasing previous region: " + aw.region.name);
                    ws.deactivateOnRelease = true;
                    ws.ReleaseResource();
                }
                RainMeadow.Debug("Requesting new region: " + region.name);
                OnlineManager.lobby.worldSessions[region.name].Request();
                OnlineManager.lobby.worldSessions[region.name].BindWorld(self.world);
            }
        }

        private void WorldLoader_ctor_RainWorldGame_Name_bool_string_Region_SetupValues(On.WorldLoader.orig_ctor_RainWorldGame_Name_bool_string_Region_SetupValues orig, WorldLoader self, RainWorldGame game, SlugcatStats.Name playerCharacter, bool singleRoomWorld, string worldName, Region region, RainWorldGame.SetupValues setupValues)
        {
            if (game?.session is OnlineGameSession os)
            {
                setupValues.worldCreaturesSpawn = os.ShouldLoadCreatures(game);
            }
            orig(self, game, playerCharacter, singleRoomWorld, worldName, region, setupValues);
            if (game?.session is OnlineGameSession)
            {
                if (game.overWorld?.activeWorld is World aw && OnlineManager.lobby.worldSessions[aw.region.name] is WorldSession ws)
                {
                    RainMeadow.Debug("Releasing previous region: " + aw.region.name);
                    ws.deactivateOnRelease = true;
                    ws.ReleaseResource();
                }
                RainMeadow.Debug("Requesting new region: " + region.name);
                OnlineManager.lobby.worldSessions[region.name].Request();
                OnlineManager.lobby.worldSessions[region.name].BindWorld(self.world);
            }
        }

        private void Room_LoadFromDataString(ILContext il)
        {
            try
            {
                // if (this.world != null && this.game != null && this.abstractRoom.firstTimeRealized && (!this.game.IsArenaSession || this.game.GetArenaGameSession.GameTypeSetup.levelItems))
                //becomes
                // if (this.world != null && this.game != null && this.abstractRoom.firstTimeRealized && (!this.game.IsOnlineSession || session.ShouldSpawnItems()) && (!this.game.IsArenaSession || this.game.GetArenaGameSession.GameTypeSetup.levelItems))
                var c = new ILCursor(il);
                var skip = il.DefineLabel();
                c.GotoNext(moveType: MoveType.After,
                    i => i.MatchLdarg(0),
                    i => i.MatchCallOrCallvirt<Room>("get_abstractRoom"),
                    i => i.MatchLdfld<AbstractRoom>("firstTimeRealized"),
                    i => i.MatchBrfalse(out skip)
                    );
                c.MoveAfterLabels();
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate((Room self) => { return self.game.session is OnlineGameSession os && !os.ShouldSpawnRoomItems(self.game); });
                c.Emit(OpCodes.Brtrue, skip);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        private void Room_Loaded(ILContext il)
        {
            try
            {
                // if (this.world != null && this.game != null && this.abstractRoom.firstTimeRealized && (!this.game.IsArenaSession || this.game.GetArenaGameSession.GameTypeSetup.levelItems))
                //becomes
                // if (this.world != null && this.game != null && this.abstractRoom.firstTimeRealized && (!this.game.IsOnlineSession || session.ShouldSpawnItems()) && (!this.game.IsArenaSession || this.game.GetArenaGameSession.GameTypeSetup.levelItems))
                var c = new ILCursor(il);
                var skip = il.DefineLabel();
                c.GotoNext(moveType: MoveType.After,
                    i => i.MatchLdarg(0),
                    i => i.MatchLdfld<Room>("roomSettings"),
                    i => i.MatchCallOrCallvirt<RoomSettings>("get_RandomItemDensity"),
                    i => i.MatchLdcR4(0f),
                    i => i.MatchBleUn(out skip)
                    );
                c.MoveAfterLabels();
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate((Room self) => { return self.game.session is OnlineGameSession os && !os.ShouldSpawnRoomItems(self.game); });
                c.Emit(OpCodes.Brtrue, skip);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }
    }
}
