using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeadlineCalendar.App;

internal static class SettingsTimeZones
{
    // Labels are geographic examples of the fixed offset, not daylight-saving zone IDs.
    // Regions currently using the same offset throughout the year avoid seasonal ambiguity.
    private static readonly (string English, string Chinese)[] Regions =
    [
        ("Baker Island", "贝克岛"),
        ("Pago Pago", "帕果帕果"),
        ("Honolulu", "檀香山"),
        ("Gambier Islands", "甘比尔群岛"),
        ("Pitcairn Islands", "皮特凯恩群岛"),
        ("Phoenix", "凤凰城"),
        ("Guatemala City", "危地马拉城"),
        ("Bogota", "波哥大"),
        ("Santo Domingo", "圣多明各"),
        ("Buenos Aires", "布宜诺斯艾利斯"),
        ("Fernando de Noronha", "费尔南多·迪诺罗尼亚"),
        ("Cape Verde", "佛得角"),
        ("Reykjavik", "雷克雅未克"),
        ("Lagos", "拉各斯"),
        ("Johannesburg", "约翰内斯堡"),
        ("Nairobi", "内罗毕"),
        ("Dubai", "迪拜"),
        ("Karachi", "卡拉奇"),
        ("Dhaka", "达卡"),
        ("Bangkok", "曼谷"),
        ("Beijing", "北京"),
        ("Tokyo", "东京"),
        ("Brisbane", "布里斯班"),
        ("Noumea", "努美阿"),
        ("Tarawa", "塔拉瓦"),
        ("Nuku'alofa", "努库阿洛法"),
        ("Kiritimati", "基里蒂马蒂岛")
    ];

    internal static IReadOnlyList<SettingsTimeZoneOption> Create(bool chinese)
    {
        var options = new List<SettingsTimeZoneOption>(Regions.Length);
        for (var index = 0; index < Regions.Length; index++)
        {
            var hours = index - 12;
            var id = string.Create(CultureInfo.InvariantCulture, $"UTC{(hours < 0 ? '-' : '+')}{Math.Abs(hours):00}:00");
            var region = chinese ? Regions[index].Chinese : Regions[index].English;
            options.Add(new SettingsTimeZoneOption(id, $"{id} ({region})"));
        }
        return options.AsReadOnly();
    }
}

internal record SettingsTimeZoneOption(string Id, string Label)
{
    public override string ToString() => Label;
}
