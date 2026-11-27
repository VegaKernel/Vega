using Terraria.ID;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

/// <summary>Prepare a complete detached inventory result before any native slot mutation.</summary>
internal static class NpcShopPurchasePlan
{
    internal const int MainSlots = 50;
    internal const int Slots = 54;
    internal readonly record struct Slot(int Type, int Stack, byte Prefix, int MaxStack);
    private static readonly int[] CoinTypes = [ItemID.CopperCoin, ItemID.SilverCoin, ItemID.GoldCoin, ItemID.PlatinumCoin];
    private static readonly long[] Values = [1, 100, 10_000, 1_000_000];

    public static NpcShopPurchaseStatus Prepare(Slot[] source, NpcShopOffer offer, int rewardMaxStack,
        int[] coinMaxStacks, out Slot[]? result)
    {
        if (offer.ItemPrice is { } itemPrice)
            return PrepareItems(source, offer, itemPrice, rewardMaxStack, out result);
        result = null;
        if (source.Length != Slots || offer.CoinPrice is not long price || price < 0 || offer.ItemType <= 0 || offer.Stack <= 0 ||
            rewardMaxStack <= 0 || coinMaxStacks.Length != CoinTypes.Length || coinMaxStacks.Any(static n => n <= 0))
            return NpcShopPurchaseStatus.InvalidOffer;
        var plan = (Slot[])source.Clone();
        long total = 0;
        for (int i = 0; i < plan.Length; i++)
        {
            Slot slot = plan[i];
            if (slot.Type == 0 && slot.Stack == 0) { plan[i] = default; continue; }
            if (slot.Type <= 0 || slot.Stack <= 0 || slot.MaxStack <= 0 || slot.Stack > slot.MaxStack)
                return NpcShopPurchaseStatus.InvalidInventory;
            int coin = Array.IndexOf(CoinTypes, slot.Type);
            if (coin < 0) continue;
            if (slot.Prefix != 0 || slot.Stack > coinMaxStacks[coin]) return NpcShopPurchaseStatus.InvalidInventory;
            total += (long)slot.Stack * Values[coin];
            if (price != 0) plan[i] = default;
        }
        if (total < price) return NpcShopPurchaseStatus.InsufficientFunds;
        if (!Add(plan, offer.ItemType, offer.Stack, offer.Prefix, rewardMaxStack, MainSlots, false))
            return NpcShopPurchaseStatus.InventoryFull;
        if (price == 0) { result = plan; return NpcShopPurchaseStatus.Committed; }
        long change = total - price;
        for (int coin = CoinTypes.Length - 1; coin >= 0; coin--)
        {
            long amount = change / Values[coin];
            change %= Values[coin];
            if (!Add(plan, CoinTypes[coin], amount, 0, coinMaxStacks[coin], Slots, true))
                return NpcShopPurchaseStatus.InventoryFull;
        }
        result = plan;
        return NpcShopPurchaseStatus.Committed;
    }

    private static NpcShopPurchaseStatus PrepareItems(Slot[] source, NpcShopOffer offer, NpcItemAmount price,
        int rewardMaxStack, out Slot[]? result)
    {
        result = null;
        if (source.Length != Slots || offer.CoinPrice is not null || !price.IsValid || offer.ItemType <= 0 ||
            offer.Stack <= 0 || rewardMaxStack <= 0) return NpcShopPurchaseStatus.InvalidOffer;
        var plan = (Slot[])source.Clone();
        int remaining = price.Amount;
        for (int i = 0; i < plan.Length; i++)
        {
            Slot slot = plan[i];
            if (slot.Type == 0 && slot.Stack == 0) { plan[i] = default; continue; }
            if (slot.Type <= 0 || slot.Stack <= 0 || slot.MaxStack <= 0 || slot.Stack > slot.MaxStack)
                return NpcShopPurchaseStatus.InvalidInventory;
            // Explicit item currencies are exact items in the main inventory. Favor existing slot
            // order and leave banks, coin slots, prefixes and all unrelated stacks untouched.
            if (i >= MainSlots || remaining == 0 || slot.Type != price.ItemType || slot.Prefix != price.Prefix) continue;
            int debit = Math.Min(remaining, slot.Stack);
            remaining -= debit;
            plan[i] = debit == slot.Stack ? default : slot with { Stack = slot.Stack - debit };
        }
        if (remaining != 0) return NpcShopPurchaseStatus.InsufficientFunds;
        if (!Add(plan, offer.ItemType, offer.Stack, offer.Prefix, rewardMaxStack, MainSlots, false))
            return NpcShopPurchaseStatus.InventoryFull;
        result = plan;
        return NpcShopPurchaseStatus.Committed;
    }

    private static bool Add(Slot[] plan, int type, long amount, byte prefix, int maxStack, int limit, bool preferCoinSlots)
    {
        for (int i = 0; i < limit && amount > 0; i++)
        {
            Slot slot = plan[i];
            if (slot.Type != type || slot.Prefix != prefix) continue;
            int addition = (int)Math.Min(amount, Math.Max(0, Math.Min(slot.MaxStack, maxStack) - slot.Stack));
            if (addition == 0) continue;
            plan[i] = slot with { Stack = slot.Stack + addition };
            amount -= addition;
        }
        for (int i = 0; i < limit && amount > 0; i++)
        {
            int index = preferCoinSlots ? (MainSlots + i) % Slots : i;
            if (plan[index].Type != 0) continue;
            int addition = (int)Math.Min(amount, maxStack);
            plan[index] = new(type, addition, prefix, maxStack);
            amount -= addition;
        }
        return amount == 0;
    }

    public static int[] ReadCoinStackLimits()
    {
        var limits = new int[CoinTypes.Length];
        for (int i = 0; i < limits.Length; i++)
        {
            var item = new global::Terraria.Item();
            item.SetDefaults(CoinTypes[i]);
            limits[i] = item.maxStack;
        }
        return limits;
    }

    public static Slot Snapshot(global::Terraria.Item item) => item.type == 0 || item.stack == 0 && item.IsAir
        ? default : new(item.type, item.stack, item.prefix, item.maxStack);
}
