"""Pure rarity-template rule; inventory/card matching belongs to the generator."""


def apply_refresh_legendary_guarantee(template, purchased_slots, rng):
    """Ranks common=0..legendary=3. Purchased positions stay inactive.

    Returns the adjusted template and chosen active slot (None if no slot).
    Always selects a lowest-rarity active slot, even if another is legendary.
    Does not consume inventory or mutate the input template.
    """
    if len(template) != 8 or any(rank not in range(4) for rank in template):
        raise ValueError('Expected eight rarity ranks from 0 through 3')
    purchased = set(purchased_slots)
    if any(i not in range(8) for i in purchased):
        raise ValueError('Purchased index outside template')
    active = [i for i in range(8) if i not in purchased]
    result = list(template)
    if not active:
        return result, None
    lowest = min(result[i] for i in active)
    chosen = rng.choice([i for i in active if result[i] == lowest])
    result[chosen] = 3
    return result, chosen
