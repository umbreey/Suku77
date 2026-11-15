using UnityEngine;
using Umbreey;

/// <summary>
/// 赌场德州桌 UI（JSON：Seat0 玩家 + Seat1=Robot1 / Seat2=Robot2 / Seat3=Robot4 / Seat4=Robot5）。
/// 布局：左二（Robot1 下、Robot2 上）+ 右二（Robot4 上、Robot5 下）。
/// 行为约定：
/// - ing_light：NPC 用小圆点；玩家用底栏扁椭圆 e_Seat0_IngLight（无单独小圆点）
/// - 当前行动座位 Image.color = 红并显示，其余隐藏或恢复默认色
/// - tip：大盲红、小盲黄并显示；非盲注隐藏
/// </summary>
[UIHandler("UICasino")]
public class UICasino : BaseUICasino
{
    public static readonly Color IngLightRed = new Color(1f, 0.15f, 0.15f, 1f);
    public static readonly Color TipBigBlindRed = new Color(1f, 0f, 0f, 1f);
    public static readonly Color TipSmallBlindYellow = new Color(1f, 1f, 0.008f, 1f);

    protected override void OnOpened()
    {
        base.OnOpened();
    }
}
