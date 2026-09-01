using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class OptionsMultiplayer : MonoBehaviour
{

	public static bool timeHost = true;
	public static bool displayName = true;
	public static bool useHighestLevel = false;
	public static bool sendLocation = true;
	public static bool sendMessage = true;
	public static bool mobileNpcSync = true;
	
	
	public static void Import(string s)
	{
		string[] list = s.Split('#');
		timeHost = list[0] == "True";
		displayName = list[1] == "True";
		useHighestLevel = list[2] == "True";
		sendLocation = list[3] == "True";
		sendMessage = list[4] == "True";

        // Backward compatibility: five-field option strings come from older builds,
        // where MobileNpcSync was always enabled.
        SetMobileNpcSync(list.Length < 6 || list[5] == "True");
	}
	
    public static void SetMobileNpcSync(bool enabled)
    {
        mobileNpcSync = enabled;

        // A joining client can spawn its local player object before the host option RPC arrives.
        // Apply the imported host policy immediately so any temporary sync state is cleaned up,
        // or so a client that locally disabled the option can start it when the host enabled it.
        MobileNpcSync.ApplySessionOption(enabled);
    }

	public static string Export()
	{
		return timeHost + "#" + displayName + "#" + useHighestLevel + '#' + sendLocation + '#' + sendMessage + '#' + mobileNpcSync;
	}
}
