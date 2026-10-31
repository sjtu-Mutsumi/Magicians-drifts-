
namespace Magicians.Core.Cards;

/// <summary>Art references are optional and independent from authoritative card rules.</summary>
public sealed record CardArtSlots(
    string IllustrationTexture = "",
    string FrameTexture = "",
    string ElementIconTexture = "",
    string EffectScene = "",
    string Sound = "");

public sealed record CardPresentationProfile(
    int SchemaVersion,
    string CardId,
    string ProfileId,
    string Name,
    string Element,
    string Rarity,
    int CombatCost,
    int BaseStudyCost,
    int Attack,
    int Defense,
    int FirstEnchantmentLayers,
    string RulesText,
    string DisplayRulesText,
    string[] RuleNotes,
    CardArtSlots Art);

public static class CardPresentationCatalog
{
    public static string ElementLabel(string element) => element switch
    { "fire" => "火", "water" => "水", "earth" => "土", "wind" => "风", "poison" => "毒", "generic" => "通用", _ => throw new InvalidDataException("Unknown card element: " + element) };
    public static string RarityLabel(string rarity) => rarity switch
    { "common" => "普通", "rare" => "稀有", "epic" => "史诗", "legendary" => "传奇", "specialization" => "专精", _ => throw new InvalidDataException("Unknown card rarity: " + rarity) };
}
