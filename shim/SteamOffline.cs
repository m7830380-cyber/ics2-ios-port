using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ICS2Shim
{
    /// <summary>
    /// Stand-in for the Steamworks calls the game makes without checking whether Steam is running
    /// (SteamUserStats achievements and SteamApps.GetCurrentGameLanguage). Achievements are remembered in
    /// Documents/achievements.txt so each one is announced once; the language follows the iOS settings.
    /// </summary>
    public static class SteamOffline
    {
        static HashSet<string> s_Unlocked;
        /// Raised when an achievement is unlocked for the first time (the iOS mod shows a toast).
        public static event Action<string> AchievementUnlocked;

        static string FilePath => Path.Combine(Application.persistentDataPath, "achievements.txt");

        static HashSet<string> Unlocked
        {
            get
            {
                if (s_Unlocked != null) return s_Unlocked;
                s_Unlocked = new HashSet<string>();
                try
                {
                    if (File.Exists(FilePath))
                        foreach (var l in File.ReadAllLines(FilePath))
                            if (l.Trim().Length > 0) s_Unlocked.Add(l.Trim());
                }
                catch (Exception e) { Debug.LogWarning("[ICS2-iOS] achievements: " + e.Message); }
                return s_Unlocked;
            }
        }

        public static bool GetAchievement(string name, out bool achieved)
        {
            achieved = name != null && Unlocked.Contains(name);
            return true;
        }

        public static bool SetAchievement(string name)
        {
            if (string.IsNullOrEmpty(name) || !Unlocked.Add(name)) return true;
            Debug.Log("[ICS2-iOS] achievement unlocked: " + name);
            try { File.AppendAllText(FilePath, name + "\n"); }
            catch (Exception e) { Debug.LogWarning("[ICS2-iOS] achievements: " + e.Message); }
            try { AchievementUnlocked?.Invoke(name); } catch { }
            return true;
        }

        public static bool StoreStats() => true;

        /// Steam language API names for the languages the game ships.
        public static string GetCurrentGameLanguage()
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.French: return "french";
                case SystemLanguage.German: return "german";
                case SystemLanguage.Spanish: return "spanish";
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.ChineseTraditional: return "schinese";
                case SystemLanguage.Japanese: return "japanese";
                case SystemLanguage.Polish: return "polish";
                case SystemLanguage.Portuguese: return "portuguese";
                case SystemLanguage.Russian: return "russian";
                case SystemLanguage.Turkish: return "turkish";
                case SystemLanguage.Korean: return "korean";
                default: return "english";
            }
        }
    }
}
