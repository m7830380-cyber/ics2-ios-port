using System;
using UnityEngine;

namespace ICS2iOS
{
    // The retail Weather widget asks api.ipify.org for the player's IP, geoplugin.net for their city and
    // openweathermap.org for the real weather there. The patcher disables that (Weather.Start does nothing);
    // this fills the widget the same way the game's success path does, with a random condition and no city.
    public static class OfflineWeather
    {
        static readonly string[] Conditions = { "clear sky", "few clouds", "light rain", "thunderstorm", "snow" };

        public static void OnSceneLoaded()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<Weather>())
            {
                if (w == null || !w.gameObject.scene.IsValid()) continue;
                try { Fill(w); }
                catch (Exception e) { Log.Warn("offline weather: " + e.Message); }
            }
        }

        static void Fill(Weather w)
        {
            var rnd = new System.Random(DateTime.Now.DayOfYear);
            int month = DateTime.Now.Month;
            bool winter = month == 12 || month <= 2;
            string cond = Conditions[rnd.Next(winter ? Conditions.Length : Conditions.Length - 1)];
            float temp = winter ? rnd.Next(-5, 6) : rnd.Next(12, 31);
            w.conditionName = cond;

            string Fix(string s) => (s ?? "").Replace("\\n", "\n");
            string desc; Sprite icon;
            switch (cond)
            {
                case "few clouds": desc = Fix(w.cloudyText); icon = w.cloudyIcon; break;
                case "light rain": desc = Fix(w.rainyText); icon = w.rainyIcon; break;
                case "thunderstorm": desc = Fix(w.thunderText); icon = w.thunderIcon; break;
                case "snow": desc = Fix(w.snowyText); icon = w.snowyIcon; break;
                default: desc = Fix(w.sunnyText); icon = w.sunnyIcon; break;
            }
            if (w.statusText) w.statusText.text = cond;
            if (w.cityText) w.cityText.text = "";
            if (w.degreeText) w.degreeText.text = temp + "°C";
            if (w.descriptionText) w.descriptionText.text = desc.ToUpper();
            if (w.currentWeatherImage && icon) w.currentWeatherImage.sprite = icon;
        }
    }
}
