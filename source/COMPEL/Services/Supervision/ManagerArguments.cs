namespace COMPEL.Services.Supervision;

/// <summary>
///     Builds the command-line arguments passed to the Heroes Of Newerth manager, faithfully reproducing the configuration flags the legacy COMPEL set via its "-execute" string.
/// </summary>
public static class ManagerArguments
{
    /// <summary>
    ///     Builds the argument vector for launching the manager: "-manager", "-noconfig", "-execute", "\"Set ...;Set ...\"", "-masterserver", "host:port".
    ///     The "-execute" payload carries its own literal double quotes because Heroes Of Newerth re-tokenises its command line and requires them; the caller passes the vector verbatim (via <see cref="ProcessStartInfo.ArgumentList"/> on Linux, or joined into <see cref="ProcessStartInfo.Arguments"/> on Windows) so those quotes survive to the process on both platforms.
    /// </summary>
    public static string[] Build(MatchServerManagerOptions options, PortPlan ports, string serverAddress, string masterServerHostAndPort, bool disableNativeRespawn = false, int? initialIdleTarget = null)
    {
        // Processor Count Governs Where Slaves May Run, Not How Many Slaves The Operator Requested. The Configured Instance Count Separately Defines The Account And Port Capacity Below.
        int processorCount = Environment.ProcessorCount;

        // The Order Of These Settings Is Immaterial: Each "Set" Command Is Applied Independently By The Manager.
        Dictionary<string, string> settings = new ()
        {
            // Append ':' So Game Server Instances Can Be Mapped To An Account Name (e.g. KONGOR:1, KONGOR:2). The Manager Appends An Incremental Index To This Value.
            ["man_masterLogin"]         = options.UserName + ":",
            ["man_masterPassword"]      = options.Password,
            ["man_numSlaveAccounts"]    = options.Instances.ToString(),
            ["man_startServerPort"]     = ports.LocalGameStart.ToString(),
            ["man_endServerPort"]       = ports.LocalGameEnd.ToString(),

            // Historically Misnamed: This Is Effectively "man_voiceStartPort". No Proxy Is Involved In The Manager's Voice Port Allocation.
            ["man_voiceProxyStartPort"] = ports.LocalVoiceStart.ToString(),
            ["man_voiceProxyEndPort"]   = ports.LocalVoiceEnd.ToString(),

            // The Manager Actively Tries To Maintain "man_maxServers" Slaves. Keep It Equal To "man_numSlaveAccounts" And The Width Of The Game/Voice Port Ranges; Using The Processor Count Here Makes CowMaster Repeatedly Launch Replacement Slaves Into Ports That The Requested Instances Already Own.
            ["man_maxServers"]          = options.Instances.ToString(),
            ["man_idleTarget"]          = (initialIdleTarget ?? options.IdleTarget).ToString(),
            ["man_enableProxy"]         = options.UseProxy ? "true" : "false",
            ["man_broadcastSlaves"]     = "true",
            ["man_autoServersPerCPU"]   = "1",
            ["man_allowCPUs"]           = string.Join(',', Enumerable.Range(0, processorCount)),

            // Shorten The Manager's Re-Authentication Retry Interval From Its Five-Minute Default. The Manager Re-Authenticates With The Master Server Only While It Is Disconnected From The Chat Server, So Lowering This Lets It Recover A Fresh Registration And Reconnect Within About Half A Minute After A Dropped Connection Instead Of Waiting Out The Default Cycle, At No Steady-State Cost. The Value Is In Milliseconds.
            ["man_reauthFrequency"]     = "30000",

            // Enables On-Demand Replay Uploads.
            ["man_uploadToS3OnDemand"]  = "1",

            // Disables Partial Replay Uploads.
            ["man_uploadToCDNOnDemand"] = "0",

            // Any Server Configuration Options Other Than The Following Are Ignored By The Manager.
            ["svr_name"]                = ServerNameWithWhitespaceWorkaround(options.ServerNamePrefix),
            ["svr_location"]            = options.Location,
            ["svr_ip"]                  = serverAddress,

            // Setting Affinity To "-1" Is Required So The Manager Can Assign Affinity To Its Child Processes.
            ["host_affinity"]           = "-1",

            ["upd_checkForUpdates"]     = "false",

            // The Port On Which COMPEL Answers Master-Server Pings. The Manager Ignores This Value; COMPEL Binds It Itself.
            ["svr_port"]                = ports.PingPort.ToString()
        };

        if (disableNativeRespawn)
        {
            // This Linux Build Can Briefly Lose A Newly-Forked Slave During Its Concurrent Startup Burst. Native Respawn Then Starts A Duplicate In The Same Slot, Which Fails Because The First Slave Still Owns The Port. Disable Respawn Only For That Startup Window; The Supervisor Restores It Through HCon After The Forks Settle So Genuine Later Slave Failures Are Still Replaced.
            settings["man_respawnServers"] = "false";
        }

        string execute = string.Join(';', settings.Select(setting => $"Set {setting.Key} {setting.Value}"));

        return
        [
            "-manager",
            "-noconfig",
            "-execute", '"' + execute + '"',
            "-masterserver", masterServerHostAndPort
        ];
    }

    /// <summary>
    ///     Appends two workaround tokens to the server name. The manager's "-execute"/"Set" handling drops the last whitespace-delimited token, and the manager drops one again when it spawns each dedicated instance, so two trailing tokens are needed for a multi-word server name to survive intact.
    /// </summary>
    public static string ServerNameWithWhitespaceWorkaround(string desiredServerName)
    {
        const string workaroundParameter = "0";

        return $"{desiredServerName} {workaroundParameter} {workaroundParameter}";
    }
}
