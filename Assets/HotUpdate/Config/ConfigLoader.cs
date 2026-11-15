using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using Luban;
using Umbreey;
using UnityEngine;

/// <summary>
/// 加载 Luban 打表产物 tables.bytes（由 SukuExcel/gen_excel.bat 生成）。
/// </summary>
public static class ConfigLoader
{
    public static Tables Tables { get; private set; }
    public static bool IsLoaded => Tables != null;

    public static void LoadFromBytes(byte[] bytes)
    {
        Tables = new Tables();
        Tables.Load(new ByteBuf(bytes));
        Log.I($"[Config] loaded tables, size={bytes.Length}");
    }

    public static DialogueLine GetDialogue(int chapter, int id)
    {
        if (Tables == null) return null;
        return chapter switch
        {
            1 => Tables.TbDialogueChapter1.GetOrDefault(id),
            2 => Tables.TbDialogueChapter2.GetOrDefault(id),
            3 => Tables.TbDialogueChapter3.GetOrDefault(id),
            4 => Tables.TbDialogueChapter4.GetOrDefault(id),
            5 => Tables.TbDialogueChapter5.GetOrDefault(id),
            _ => null
        };
    }

    public static List<DialogueLine> GetChapterLines(int chapter)
    {
        if (Tables == null) return new List<DialogueLine>();
        IReadOnlyList<DialogueLine> list = chapter switch
        {
            1 => Tables.TbDialogueChapter1.DataList,
            2 => Tables.TbDialogueChapter2.DataList,
            3 => Tables.TbDialogueChapter3.DataList,
            4 => Tables.TbDialogueChapter4.DataList,
            5 => Tables.TbDialogueChapter5.DataList,
            _ => null
        };
        if (list == null || list.Count == 0) return new List<DialogueLine>();
        return list.OrderBy(x => x.Order).ThenBy(x => x.Id).ToList();
    }

    /// <summary>章内 order 最小的一句，作为事件入口。</summary>
    public static DialogueLine GetFirstDialogue(int chapter)
    {
        var lines = GetChapterLines(chapter);
        return lines.Count > 0 ? lines[0] : null;
    }

    public static GlobalConfig GetGlobal()
    {
        return Tables?.TbGlobal.GetOrDefault(1);
    }

    public static Item GetItem(int id)
    {
        return Tables?.TbItem.GetOrDefault(id);
    }

    public static IReadOnlyList<Item> GetAllItems()
    {
        return Tables?.TbItem.DataList ?? (IReadOnlyList<Item>)System.Array.Empty<Item>();
    }

    public static DropGroup GetDropGroup(int id)
    {
        return Tables?.TbDropGroup.GetOrDefault(id);
    }

    public static List<DropEntry> GetDropEntries(int groupId)
    {
        if (Tables == null) return new List<DropEntry>();
        return Tables.TbDropEntry.DataList.Where(e => e.GroupId == groupId).ToList();
    }

    public static Location GetLocation(int id)
    {
        return Tables?.TbLocation.GetOrDefault(id);
    }

    public static Location GetLocationByEnum(LocationId locationId)
    {
        if (Tables == null) return null;
        return Tables.TbLocation.DataList.FirstOrDefault(l => l.LocationId == locationId);
    }

    public static bool IsLocationAllowed(Location location, DayPhase phase)
    {
        if (location == null || location.AllowPhases == null || location.AllowPhases.Count == 0)
            return true;
        return location.AllowPhases.Contains(phase);
    }

    public static CasinoRule GetCasinoRule(int id = 1)
    {
        return Tables?.TbCasinoRule.GetOrDefault(id);
    }

#if UNITY_EDITOR
    public static bool TryLoadFromEditorFile()
    {
        var path = Path.Combine(Application.dataPath, "AssetBundles/Config/tables.bytes");
        if (!File.Exists(path))
        {
            Log.W($"[Config] missing {path}, run SukuExcel/gen_excel.bat first");
            return false;
        }
        LoadFromBytes(File.ReadAllBytes(path));
        return true;
    }
#endif
}
