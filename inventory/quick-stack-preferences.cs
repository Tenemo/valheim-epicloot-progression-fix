using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EpicLootProgressionFix
{
    // Read the three collections written by QuickStackStore.UserConfig.Save without running a deserializer or loading types.
    // Record layout: https://learn.microsoft.com/openspecs/windows_protocols/ms-nrbf/954a0657-b901-4813-9398-4ec732fe8b32
    internal sealed class QuickStackPreferences
    {
        internal int[][] Slots = Array.Empty<int[]>();
        internal string[] Favorites = Array.Empty<string>(), Trash = Array.Empty<string>();

        internal static QuickStackPreferences Read(string path)
        {
            try { using (var stream = File.OpenRead(path)) return Read(stream); }
            catch (FileNotFoundException) { return new QuickStackPreferences(); }
            catch (DirectoryNotFoundException) { return new QuickStackPreferences(); }
        }

        internal static QuickStackPreferences Read(Stream stream)
        {
            if (stream.Length == 0 || stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid Quick Stack preference file size.");
            var reader = new Records(stream);
            var positions = reader.Collection("System.Collections.Generic.List`1[[System.Tuple`2[[System.Int32,");
            var slots = positions.Select(value =>
            {
                var tuple = value as Node;
                if (tuple == null || !tuple.Name.StartsWith("System.Tuple`2[[System.Int32,", StringComparison.Ordinal) ||
                    !tuple.Values.TryGetValue("m_Item1", out var x) || !(x is int column) || column < 0 ||
                    !tuple.Values.TryGetValue("m_Item2", out var y) || !(y is int row) || row < 0)
                    throw new InvalidDataException("Invalid Quick Stack favorite slot.");
                return new[] { column, row };
            }).ToArray();
            string[] Names()
            {
                var values = reader.Collection("System.Collections.Generic.List`1[[System.String,");
                if (values.Any(value => !(value is string))) throw new InvalidDataException("Invalid Quick Stack item names.");
                return values.Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
            }
            var result = new QuickStackPreferences { Slots = slots, Favorites = Names(), Trash = Names() };
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected data after Quick Stack preferences.");
            return result;
        }

        private sealed class Node
        {
            internal string Name;
            internal string[] Members;
            internal byte[] PrimitiveTypes;
            internal readonly Dictionary<string, object> Values = new Dictionary<string, object>();
        }
        private sealed class Reference { internal int Id; }
        private sealed class Nulls { internal int Count; }

        private sealed class Records
        {
            private readonly BinaryReader reader;
            private readonly Dictionary<int, object> objects = new Dictionary<int, object>();
            private int depth;
            private int remainingEntries = 1000000;
            internal Records(Stream stream) => reader = new BinaryReader(stream, new UTF8Encoding(false, true), true);
            private string String()
            {
                int length = 0;
                for (int shift = 0; shift <= 28; shift += 7)
                {
                    byte part = reader.ReadByte();
                    if (shift == 28 && part > 7) break;
                    length |= (part & 127) << shift;
                    if ((part & 128) != 0) continue;
                    if (length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid Quick Stack string length.");
                    byte[] bytes = reader.ReadBytes(length);
                    if (bytes.Length != length) throw new EndOfStreamException();
                    return new UTF8Encoding(false, true).GetString(bytes);
                }
                throw new InvalidDataException("Invalid Quick Stack string length.");
            }
            private int Count()
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > remainingEntries) throw new InvalidDataException("Invalid Quick Stack collection length.");
                remainingEntries -= count;
                return count;
            }
            private object Resolve(object value)
            {
                if (value is Reference reference)
                {
                    if (!objects.TryGetValue(reference.Id, out value) || value is Reference)
                        throw new InvalidDataException("Invalid Quick Stack object reference.");
                }
                return value;
            }
            internal object[] Collection(string expected)
            {
                objects.Clear();
                if (reader.ReadByte() != 0) throw new InvalidDataException("Missing Quick Stack collection header.");
                int root = reader.ReadInt32();
                reader.ReadInt32();
                if (reader.ReadInt32() != 1 || reader.ReadInt32() != 0) throw new InvalidDataException("Unsupported Quick Stack collection format.");
                while (true)
                {
                    byte tag = reader.ReadByte();
                    if (tag == 11) break;
                    Record(tag);
                }
                var list = Resolve(new Reference { Id = root }) as Node;
                if (list == null || !list.Name.StartsWith(expected, StringComparison.Ordinal) ||
                    !list.Values.TryGetValue("_items", out var items) || !(Resolve(items) is object[] array) ||
                    !list.Values.TryGetValue("_size", out var size) || !(size is int length) || length < 0 || length > array.Length)
                    throw new InvalidDataException("Invalid Quick Stack preference collection.");
                return array.Take(length).Select(Resolve).ToArray();
            }
            private byte Type(byte type)
            {
                switch (type)
                {
                    case 0: return reader.ReadByte();
                    case 1: case 2: case 5: case 6: return 0;
                    case 3: String(); return 0;
                    case 4: String(); reader.ReadInt32(); return 0;
                    default: throw new InvalidDataException("Unsupported Quick Stack member type.");
                }
            }
            private object Primitive(byte type)
            {
                if (type != 8) throw new InvalidDataException("Unsupported Quick Stack primitive.");
                return reader.ReadInt32();
            }
            private object Value()
            {
                byte tag;
                while ((tag = reader.ReadByte()) == 12) { reader.ReadInt32(); String(); }
                return Record(tag);
            }
            private object Register(int id, object value)
            {
                if (--remainingEntries < 0) throw new InvalidDataException("Too many Quick Stack preference records.");
                if (objects.ContainsKey(id)) throw new InvalidDataException("Duplicate Quick Stack object identifier.");
                objects.Add(id, value);
                return value;
            }
            private object Record(byte tag)
            {
                if (++depth > 64) throw new InvalidDataException("Quick Stack preferences are nested too deeply.");
                try
                {
                    if (tag == 12) { reader.ReadInt32(); String(); return null; }
                    if (tag == 9) return new Reference { Id = reader.ReadInt32() };
                    if (tag == 10) return null;
                    if (tag == 13 || tag == 14) return new Nulls { Count = tag == 13 ? reader.ReadByte() : Count() };
                    if (tag == 8) return Primitive(reader.ReadByte());
                    int id = reader.ReadInt32();
                    if (tag == 6) return Register(id, String());
                    if (tag == 1 || tag == 4 || tag == 5)
                    {
                        Node node;
                        if (tag == 1)
                        {
                            if (!(Resolve(new Reference { Id = reader.ReadInt32() }) is Node metadata))
                                throw new InvalidDataException("Invalid Quick Stack class metadata.");
                            node = new Node { Name = metadata.Name, Members = metadata.Members, PrimitiveTypes = metadata.PrimitiveTypes };
                        }
                        else
                        {
                            string name = String();
                            int count = Count();
                            if (count > 16) throw new InvalidDataException("Unexpected Quick Stack class fields.");
                            var names = Enumerable.Range(0, count).Select(_ => String()).ToArray();
                            var types = reader.ReadBytes(count);
                            if (types.Length != count) throw new EndOfStreamException();
                            node = new Node { Name = name, Members = names, PrimitiveTypes = types.Select(Type).ToArray() };
                            if (tag == 5) reader.ReadInt32();
                        }
                        Register(id, node);
                        for (int index = 0; index < node.Members.Length; index++)
                            node.Values.Add(node.Members[index], node.PrimitiveTypes[index] == 0 ? Value() : Primitive(node.PrimitiveTypes[index]));
                        return node;
                    }
                    if (tag == 7 || tag == 16 || tag == 17)
                    {
                        if (tag == 7 && (reader.ReadByte() != 0 || reader.ReadInt32() != 1))
                            throw new InvalidDataException("Unsupported Quick Stack array shape.");
                        int count = Count();
                        byte primitive = tag == 7 ? Type(reader.ReadByte()) : (byte)0;
                        var array = new object[count];
                        Register(id, array);
                        for (int index = 0; index < count; index++)
                        {
                            object value = primitive == 0 ? Value() : Primitive(primitive);
                            if (value is Nulls empty)
                            {
                                if (empty.Count < 1 || empty.Count > count - index) throw new InvalidDataException("Invalid Quick Stack null run.");
                                index += empty.Count - 1;
                            }
                            else array[index] = value;
                        }
                        return array;
                    }
                    throw new InvalidDataException("Unsupported Quick Stack record: " + tag);
                }
                finally { depth--; }
            }
        }
    }
}
