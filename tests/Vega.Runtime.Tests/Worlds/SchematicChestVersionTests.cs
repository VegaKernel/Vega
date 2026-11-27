using System.Buffers.Binary;
using NUnit.Framework;
using TerraRuntime.Schematics;
using Vega.Contracts.Scenes;
using Vega.Modules.Scenes;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
public sealed class SchematicChestVersionTests
{
    // Independently encoded TRSC v1 fixture: one air tile, a chest with a single
    // represented item, and six empty required sections. Fixed CRCs and offsets.
    private const string LegacyFixture = "VFJTQwEAIAAXAQAAAQAAAAEAAAAAAAAAAAAAAAgAAAABAAEA4AAAABQAAAAUAAAAy6CyagAAAAACAAEA9AAAACEAAAAhAAAAoC45VwAAAAADAAEAFQEAAAQAAAAEAAAAHN9EIQAAAAAEAAEAGQEAAAQAAAAEAAAAHN9EIQAAAAAFAAEAHQEAAAQAAAAEAAAAHN9EIQAAAAAGAAEAIQEAAAQAAAAEAAAAHN9EIQAAAAAHAAEAJQEAAAQAAAAEAAAAHN9EIQAAAAAIAAEAKQEAAAQAAAAEAAAAHN9EIQAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAQAAABMb290AQAAAAgAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    [Test]
    public void LegacyDecodeAndReserializePreservesExactWireBytes()
    {
        byte[] bytes = Convert.FromBase64String(LegacyFixture);
        var decoded = SchematicBinary.Deserialize(bytes);
        Assert.That(decoded.Chests.Single().Capacity, Is.EqualTo(40));
        Assert.That(decoded.Chests.Single().Items, Is.EqualTo(new[] { new SchematicItemStack(8, 4) }));
        Assert.That(SchematicBinary.Serialize(decoded), Is.EqualTo(bytes));
        var corrupted = (byte[])bytes.Clone();
        SetChestWord(corrupted, 20, 41); // legacy item-count field, valid CRC but outside v1 bounds
        Assert.That(() => SchematicBinary.Deserialize(corrupted), Throws.TypeOf<SchematicFormatException>());
    }

    [Test]
    public void ExpandedRoundtripPreservesCapacityAndIndexedContentsWithoutChangingLegacyRecords()
    {
        var items = new SchematicItemStack[200];
        items[40] = new(8, 2);
        items[199] = new(1, 9, 2);
        var document = Document(new(0, 0, "Loot", items) { Capacity = 200 },
            new(1, 0, "Empty expansion", []) { Capacity = 80 }, new(2, 0, "Legacy", [new(8, 4)]));
        byte[] bytes = SchematicBinary.Serialize(document);
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)), Is.EqualTo(2));
        var decoded = SchematicBinary.Deserialize(bytes);
        Assert.That(decoded.Chests.Select(chest => chest.Capacity), Is.EqualTo(new[] { 200, 80, 40 }));
        Assert.That(decoded.Chests[0].Items, Is.EqualTo(items));
        Assert.That(decoded.Chests[1].Items, Is.Empty);
        Assert.That(SchematicBinary.Serialize(decoded), Is.EqualTo(bytes));
        Assert.That(() => SchematicSceneProjection.Create(decoded, "preview", 10, 20, SceneObserverScope.Global),
            Throws.TypeOf<SchematicFormatException>().With.Message.Contains("virtual preview"));
        var native = SchematicSceneProjection.CreateMaterialization(decoded, 10, 20);
        Assert.That(native.Bounds, Is.EqualTo(new SceneBounds(10, 20, 3, 1)));
        Assert.That(native.Tiles.Tiles.Count, Is.EqualTo(3));
    }

    [TestCase(0)]
    [TestCase(39)]
    [TestCase(201)]
    [TestCase(int.MaxValue)]
    public void InvalidCapacityRejectedOnWriteAndRead(int capacity)
    {
        Assert.That(() => SchematicBinary.Serialize(Document(new SchematicChest(0, 0, "Loot", []) { Capacity = capacity })),
            Throws.TypeOf<SchematicFormatException>());
        byte[] bytes = SchematicBinary.Serialize(Document(new SchematicChest(0, 0, "Loot", []) { Capacity = 80 }));
        SetChestWord(bytes, 20, capacity);
        Assert.That(() => SchematicBinary.Deserialize(bytes), Throws.TypeOf<SchematicFormatException>());
    }

    [Test]
    public void ItemCountCannotExceedCapacityRegardlessOfVersion()
    {
        Assert.That(() => SchematicBinary.Serialize(Document(new SchematicChest(0, 0, "Loot", new SchematicItemStack[81]) { Capacity = 80 })),
            Throws.TypeOf<SchematicFormatException>());
        byte[] bytes = SchematicBinary.Serialize(Document(new SchematicChest(0, 0, "Loot", new SchematicItemStack[80]) { Capacity = 80 }));
        SetChestWord(bytes, 20, 79);
        Assert.That(() => SchematicBinary.Deserialize(bytes), Throws.TypeOf<SchematicFormatException>());
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 3);
        Assert.That(() => SchematicBinary.Deserialize(bytes), Throws.TypeOf<SchematicFormatException>());
    }

    private static SchematicDocument Document(params SchematicChest[] chests) => new()
    {
        ContentVersion = 279, Width = 3, Height = 1, Tiles = new SchematicTile[3], Chests = chests
    };

    private static void SetChestWord(byte[] bytes, int relativeOffset, int value)
    {
        // The second required directory entry is Chests. Recompute the section
        // CRC deliberately to exercise semantic bounds rather than checksum failure.
        Span<byte> entry = bytes.AsSpan(SchematicBinary.HeaderSize + SchematicBinary.DirectoryEntrySize, SchematicBinary.DirectoryEntrySize);
        int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]));
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + relativeOffset), value);
        uint crc = uint.MaxValue;
        foreach (byte b in bytes.AsSpan(offset, length))
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], ~crc);
    }
}
