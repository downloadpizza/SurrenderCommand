using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using CommandMod.CommandHandler;
using BepInEx.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System;
using System.CodeDom;
using System.Collections;
using UnityEngine;

namespace SurrenderCommand;

public class Config
{
    public ConfigEntry<double> RequiredVotes;
    public ConfigEntry<int> Timeout;
    public ConfigEntry<int> Cooldown;
    public ConfigEntry<int> StartLockout;

    public static Config Load(ConfigFile cfgFile)
    {
        return new Config
        {
            RequiredVotes = cfgFile.Bind(
                "General",
                "RequiredVotes",
                1.0, // by default require unanimous vote.
                new ConfigDescription("The ratio of yes votes to players on the server required to surrender", new AcceptableValueRange<double>(0.0, 1.0))
            ),

            Timeout = cfgFile.Bind(
                "General",
                "Timeout",
                30,
                "How long to wait in seconds until all non-voters default to a no surrender vote"
            ),

            Cooldown = cfgFile.Bind(
                "General",
                "Cooldown",
                300,
                "How long to wait in seconds until a failed surrender vote can be retried"
            ),

            StartLockout = cfgFile.Bind(
                "General",
                "StartLockout",
                1200, // 20 minutes by default
                "How long a game needs to be running before surrenders become available"
            )
        };
    }
}

[BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
[BepInDependency("me.muj.commandmod")]
public class Plugin : BaseUnityPlugin
{
    internal new ManualLogSource Logger;
    internal new Config Config;
    internal State State;
    internal static Plugin Instance;

    private void Awake()
    {
        // Plugin startup logic
        Instance = this;
        Logger = base.Logger;
        Config = Config.Load(base.Config);
        State = new State();

        GameObject obj = new("UpdateHandler")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        obj.AddComponent<UpdateComponent>();
        DontDestroyOnLoad(obj);
    }
}

public class UpdateComponent : MonoBehaviour
{
    private void Start()
    {
        StartCoroutine(UpdateLoop());
    }

    private IEnumerator UpdateLoop()
    {
        while (true)
        {
            if (GameManager.gameState != GameManager.GameState.Multiplayer)
            {
                // clear faction surrenders when out of game
                Plugin.Instance.State.FactionSurrenders = [];
                Plugin.Instance.State.TimeSinceStart.Reset();
            }
            else
            {
                Plugin.Instance.State.TimeSinceStart.Start();
                Plugin.Instance.State.FactionSurrenders.Values.ToList().ForEach(surr => surr.Update());
            }
            yield return new WaitForSeconds(1f);
        }
    }
}

class State
{
    public Stopwatch TimeSinceStart = new();
    public Dictionary<FactionHQ, SurrenderState> FactionSurrenders = [];
}

enum TimerState
{
    READY,
    RUNNING,
    DONE,
    COOLDOWN
}

class Timer(long durationMs, long cooldownMs)
{
    private TimerState _timerState = TimerState.READY;
    public TimerState TimerState
    {
        get
        {
            CheckState();
            return _timerState;
        }
    }

    private readonly Stopwatch _stopwatch = new();
    private readonly long _durationMs = durationMs;
    private readonly long _cooldownMs = cooldownMs;


    public long RemainingCooldown => TimerState == TimerState.COOLDOWN ? (_cooldownMs - _stopwatch.ElapsedMilliseconds) : -1;

    public bool Start()
    {
        CheckState();
        if (_timerState != TimerState.READY)
        {
            return false;
        }
        _timerState = TimerState.RUNNING;
        _stopwatch.Start();
        return true;
    }

    public bool StartCooldown()
    {
        CheckState();
        if (_timerState != TimerState.DONE)
        {
            return false;
        }
        _timerState = TimerState.COOLDOWN;
        _stopwatch.Restart();
        return true;
    }

    private void CheckState()
    {
        switch (_timerState)
        {
            case TimerState.READY:
            case TimerState.DONE:
                break;
            case TimerState.RUNNING:
                if (_stopwatch.ElapsedMilliseconds >= _durationMs)
                {
                    _stopwatch.Reset();
                    _timerState = TimerState.DONE;
                }
                break;
            case TimerState.COOLDOWN:
                if (_stopwatch.ElapsedMilliseconds >= _cooldownMs)
                {
                    _stopwatch.Reset();
                    _timerState = TimerState.READY;
                }
                break;
        }
    }

    internal void Skip()
    {
        if (_timerState == TimerState.RUNNING)
        {
            _stopwatch.Reset();
            _timerState = TimerState.DONE;
        }
    }

}

class SurrenderState
{
    public FactionHQ Faction;
    public Dictionary<Player, bool> Surrendered = [];
    public Timer Timer = new(Plugin.Instance.Config.Timeout.Value * 1000L, Plugin.Instance.Config.Cooldown.Value * 1000L);

    public void SetPlayer(Player p, bool surrender)
    {
        Surrendered[p] = surrender;
    }

    public void Start()
    {
        if (Timer.Start())
        {
            Wrapper.SendMessageToFaction(Faction, $"A surrender vote has been started. Use command surrender or nosurrender.", GameManager.LocalPlayer);
        }
    }

    public bool IsReady => Timer.TimerState == TimerState.READY;
    public bool IsActive => Timer.TimerState == TimerState.RUNNING;
    public bool IsDone => Timer.TimerState == TimerState.DONE;
    public bool IsCooldown => Timer.TimerState == TimerState.COOLDOWN;

    public void Update()
    {
        if (IsActive)
        {
            var totalPlayers = Faction.GetPlayers(false).Count();
            var totalVotes = Surrendered.Count();

            if (totalVotes >= totalPlayers)
            {
                Timer.Skip();
            }
        }
        if (IsDone)
        {
            Plugin.Instance.Logger.LogDebug($"Counting votes...");

            // count votes:
            var totalPlayers = Faction.GetPlayers(false).Count();
            var surrenderYes = Surrendered.Count((entry) => entry.Value);

            var ratio = ((double)surrenderYes) / totalPlayers;
            var required = Plugin.Instance.Config.RequiredVotes.Value;

            Plugin.Instance.Logger.LogDebug($"Vote count result: {ratio}, {(int)Math.Round(required * 100)}%, {ratio >= required}");

            if (ratio >= required)
            {
                Wrapper.SendMessageToAll($"{Faction.faction.factionName} has surrendered ({surrenderYes}/{totalPlayers}).", GameManager.LocalPlayer);
                Faction.DeclareEndGame(NuclearOption.SavedMission.ObjectiveV2.Outcomes.EndType.Defeat);
            }
            else
            {
                Wrapper.SendMessageToFaction(Faction, $"Surrender vote has failed ({surrenderYes}/{totalPlayers}), Required: {(int)Math.Round(required * 100)}%", GameManager.LocalPlayer);
            }

            Timer.StartCooldown();
            Surrendered.Clear();
        }
    }
}

static class PluginInfo
{
    public const string GUID = "net.downloadpizza.SurrenderCommand";
    public const string NAME = "Surrender Command";
    public const string VERSION = "0.0.1";
}


public static class Commands
{
    private static string RenderMilliseconds(long ms)
    {
        var time = new TimeSpan(ms * TimeSpan.TicksPerMillisecond);

        if (time.TotalSeconds < 60)
            return $"{time.Seconds}s";
        if (time.TotalMinutes < 60)
            return $"{time.Minutes}m {time.Seconds}s";

        return $"{time.Hours}h {(time.Minutes > 0 ? $"{time.Minutes}m " : "")}{time.Seconds}s";
    }

    [ConsoleCommand("surrender")]
    public static void Surrender(string[] args, CommandObjects co)
    {
        Plugin.Instance.Logger.LogDebug($"{co.Player.PlayerName} is trying to start a surrender");

        var factionHQ = co.Player.HQ;
        if (factionHQ == null)
        {
            Wrapper.SendMessageToPlayer(co.Player, $"Can't surrender without joining a team", GameManager.LocalPlayer, true);
            return;
        }

        if (Plugin.Instance.State.TimeSinceStart.ElapsedMilliseconds <= Plugin.Instance.Config.StartLockout.Value * 1000L)
        {
            var remainingLockout = (Plugin.Instance.Config.StartLockout.Value * 1000L) - Plugin.Instance.State.TimeSinceStart.ElapsedMilliseconds;
            Wrapper.SendMessageToPlayer(co.Player, $"Can't surrender at the start of the game. Wait another {RenderMilliseconds(remainingLockout)}", GameManager.LocalPlayer, true);
            return;
        }
        if (!Plugin.Instance.State.FactionSurrenders.ContainsKey(factionHQ))
        {
            Plugin.Instance.Logger.LogDebug($"Creating new surrenderState for {factionHQ.faction.factionName}");
            Plugin.Instance.State.FactionSurrenders[factionHQ] = new SurrenderState { Faction = factionHQ };
        }
        var surrenderState = Plugin.Instance.State.FactionSurrenders[factionHQ];
        Plugin.Instance.Logger.LogDebug($"Before Start, TimerState is {surrenderState.Timer.TimerState}");

        if (surrenderState.IsReady)
        {
            surrenderState.Start();
        }

        Plugin.Instance.Logger.LogDebug($"After Start, TimerState is {surrenderState.Timer.TimerState}");

        if (surrenderState.IsCooldown)
        {
            var remainingCooldown = surrenderState.Timer.RemainingCooldown;
            Wrapper.SendMessageToPlayer(co.Player, $"Surrender is in cooldown. Wait {RenderMilliseconds(remainingCooldown)}", GameManager.LocalPlayer, true);
            return;
        }

        surrenderState.SetPlayer(co.Player, true);
    }

    [ConsoleCommand("nosurrender")]
    public static void NoSurrender(string[] args, CommandObjects co)
    {
        var factionHQ = co.Player.HQ;
        if (factionHQ == null)
        {
            Wrapper.SendMessageToPlayer(co.Player, $"Can't surrender without joining a team", GameManager.LocalPlayer, true);
            return;
        }
        if (!Plugin.Instance.State.FactionSurrenders.ContainsKey(factionHQ))
        {
            Plugin.Instance.State.FactionSurrenders[factionHQ] = new SurrenderState { Faction = factionHQ };
        }
        var surrenderState = Plugin.Instance.State.FactionSurrenders[factionHQ];
        if (!surrenderState.IsActive)
        {
            Wrapper.SendMessageToPlayer(co.Player, "There is no surrender vote currently active", GameManager.LocalPlayer, true);
            return;
        }

        surrenderState.SetPlayer(co.Player, false);
    }
}

public class Wrapper
{
    private static readonly FieldInfo _chatManager = AccessTools.Field(typeof(ChatManager), "i");
    public static ChatManager ChatManager
    {
        get => (ChatManager)_chatManager.GetValue(null);
        set
        {
            if (value != null)
                _chatManager.SetValue(null, value);
        }
    }


    public static void SendMessageToPlayer(Player player, string msg, Player from, bool allChat)
    {
        ChatManager.TargetReceiveMessage(player.Owner, msg, from, allChat);
    }
    public static void SendMessageToFaction(FactionHQ factionHQ, string message, Player from)
    {
        foreach (Player value in UnitRegistry.playerLookup.Values)
        {
            if (value.HQ == factionHQ)
            {
                SendMessageToPlayer(value, message, from, false);
            }
        }
    }

    public static void SendMessageToAll(string message, Player from)
    {
        foreach (Player value in UnitRegistry.playerLookup.Values)
        {
            SendMessageToPlayer(value, message, from, true);
        }
    }
}