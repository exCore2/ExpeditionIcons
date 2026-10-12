using System.Collections.Generic;
using System.Linq;
using ExileCore2.PoEMemory.FilesInMemory;

namespace ExpeditionIcons.PathPlannerData;

/// <summary>
/// One recipe a runestone could be completed with, reduced to what scoring needs.
/// </summary>
/// <param name="Recipe">Kept for the expected-choice indicator, not used by scoring.</param>
/// <param name="Price">The static loot drop's value.</param>
/// <param name="RecipeRuneMask">Every rune in the recipe - scales this runestone's own monsters.</param>
/// <param name="PassedOnMask">Runes at the passed-on positions - scale later runestones' monsters.</param>
/// <param name="PassedOnProduct">Product of the passed-on runes' multipliers, precomputed for candidate ranking and display.</param>
/// <param name="RecipeRuneProduct">
/// Product of every recipe rune's multiplier - what this runestone's own monsters are scaled by. Ranks
/// candidates below the value threshold, where price carries no information.
/// </param>
/// <param name="RuneCount">
/// How many runes the recipe uses. Breaks ties between candidates whose multipliers all come out equal,
/// which is the normal case when no rune weights are configured.
/// </param>
/// <param name="MonsterRuneCount">
/// How many of those runes are actually consumed here - the recipe's length minus the runes at this
/// runestone's passed-on positions. This is what the spawned monsters scale by, linearly: a five-rune
/// recipe with two runes passed on spawns three runic monsters' worth, whatever the stone's socket
/// count is. Passed-on runes are deliberately not counted anywhere - they multiply later monsters
/// through the rune mask rather than adding monsters of their own.
/// </param>
/// <param name="EquivalentRecipes">
/// Every recipe this candidate stands for, including <see cref="Recipe"/> itself. Candidates that agree on
/// price and both masks are interchangeable to the search, so they are deduped into one - but the game may
/// offer any member of that group, and the indicator has to recognise all of them. Populated while the
/// candidate list is built and only read afterwards.
/// </param>
public record RunestoneCandidate(
    Expedition2Recipe Recipe,
    double Price,
    ulong RecipeRuneMask,
    ulong PassedOnMask,
    double PassedOnProduct,
    double RecipeRuneProduct,
    int RuneCount,
    int MonsterRuneCount,
    IReadOnlyList<Expedition2Recipe> EquivalentRecipes)
{
    /// <summary>True when <paramref name="recipe"/> is this candidate, or one the dedupe folded into it.</summary>
    public bool Matches(Expedition2Recipe recipe)
    {
        return recipe != null && (ReferenceEquals(Recipe, recipe) || EquivalentRecipes.Contains(recipe));
    }
}
