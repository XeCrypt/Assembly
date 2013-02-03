using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using ExtryzeDLL.Blam;
using ExtryzeDLL.Blam.ThirdGen;
using ExtryzeDLL.IO;
using Assembly.Helpers;

namespace Assembly.Metro.Controls.PageTemplates.Games.Components.MetaData
{
    public class MetaReader : IMetaFieldVisitor
    {
        private IStreamManager _streamManager;
        private IReader _reader;
        private uint _baseOffset = 0;
        private ICacheFile _cache;
        private FieldChangeSet _ignoredFields;

        public MetaReader(IStreamManager streamManager, uint baseOffset, ICacheFile cache)
            : this(streamManager, baseOffset, cache, null)
        {
        }

        public MetaReader(IStreamManager streamManager, uint baseOffset, ICacheFile cache, FieldChangeSet ignore)
        {
            _streamManager = streamManager;
            _baseOffset = baseOffset;
            _cache = cache;
            _ignoredFields = ignore;
        }

        private void ReadField(MetaField field)
        {
            // Update the field's memory address
            ValueField valueField = field as ValueField;
            if (valueField != null)
                valueField.FieldAddress = _cache.MetaPointerConverter.OffsetToAddress(_baseOffset + valueField.Offset);

            // Read its contents if it has changed (or if change detection is disabled)
            if (_ignoredFields == null || !_ignoredFields.HasChanged(field))
                field.Accept(this);

            // If it's a reflexive, read its children
            ReflexiveData reflexive = field as ReflexiveData;
            if (reflexive != null)
                ReadReflexiveChildren(reflexive);
        }

        public void ReadFields(IList<MetaField> fields)
        {
            bool opened = OpenReader();
            try
            {
                for (int i = 0; i < fields.Count; i++)
                    ReadField(fields[i]);
            }
            finally
            {
                if (opened)
                    CloseReader();
            }
        }

        public void ReadReflexiveChildren(ReflexiveData reflexive)
        {
            if (!reflexive.HasChildren || reflexive.CurrentIndex < 0)
                return;

            bool opened = OpenReader();
            try
            {
                // Calculate the base offset to read from
                uint oldBaseOffset = _baseOffset;
                uint dataOffset = _cache.MetaPointerConverter.AddressToOffset(reflexive.FirstEntryAddress);
                _baseOffset = (uint)(dataOffset + reflexive.CurrentIndex * reflexive.EntrySize);

                ReflexivePage page = reflexive.Pages[reflexive.CurrentIndex];
                for (int i = 0; i < page.Fields.Length; i++)
                {
                    if (page.Fields[i] != null)
                        ReadField(page.Fields[i]);
                    else
                        ReadField(reflexive.Template[i]);
                }

                _baseOffset = oldBaseOffset;
            }
            finally
            {
                if (opened)
                    CloseReader();
            }
        }

        public void VisitBitfield(BitfieldData field)
        {
            SeekToOffset(field.Offset);
            switch (field.Type)
            {
                case BitfieldType.Bitfield8:
                    field.Value = (uint)_reader.ReadByte();
                    break;
                case BitfieldType.Bitfield16:
                    field.Value = (uint)_reader.ReadUInt16();
                    break;
                case BitfieldType.Bitfield32:
                    field.Value = _reader.ReadUInt32();
                    break;
            }
        }

        public void VisitComment(CommentData field)
        {
        }

        public void VisitEnum(EnumData field)
        {
            SeekToOffset(field.Offset);
            switch (field.Type)
            {
                case EnumType.Enum8:
                    field.Value = (int)_reader.ReadSByte();
                    break;
                case EnumType.Enum16:
                    field.Value = (int)_reader.ReadInt16();
                    break;
                case EnumType.Enum32:
                    field.Value = _reader.ReadInt32();
                    break;
            }

            // Search for the corresponding option and select it
            EnumValue selected = null;
            foreach (EnumValue option in field.Values)
            {
                if (option.Value == field.Value)
                    selected = option;
            }
            if (selected == null)
            {
                // Nothing matched, so just add an option for it
                selected = new EnumValue(field.Value.ToString(), field.Value);
                field.Values.Add(selected);
            }
            field.SelectedValue = selected;
        }

        public void VisitUint8(Uint8Data field)
        {
            SeekToOffset(field.Offset);
            field.Value = _reader.ReadByte();
        }

        public void VisitInt8(Int8Data field)
        {
            SeekToOffset(field.Offset);
            field.Value = _reader.ReadSByte();
        }

        public void VisitUint16(Uint16Data field)
        {
            SeekToOffset(field.Offset);
            field.Value = _reader.ReadUInt16();
        }

        public void VisitInt16(Int16Data field)
        {
            SeekToOffset(field.Offset);
            field.Value = _reader.ReadInt16();
        }

        public void VisitUint32(Uint32Data field)
        {
            SeekToOffset(field.Offset);
            field.Value = _reader.ReadUInt32();
        }

        public void VisitInt32(Int32Data field)
        {
            SeekToOffset(field.Offset);
            field.Value = _reader.ReadInt32();
        }


        public void VisitString(StringData field)
        {
            SeekToOffset(field.Offset);
            field.Value = _reader.ReadAscii(field.Length);
        }
        public void VisitStringID(StringIDData field)
        {
            SeekToOffset(field.Offset);
            field.Value = _cache.StringIDs.StringIDToIndex(new StringID(_reader.ReadInt32()));
        }

        public void VisitRawData(RawData field)
        {
            SeekToOffset(field.Offset);
            field.Value = ExtryzeDLL.Util.FunctionHelpers.BytesToHexLines(_reader.ReadBlock(field.Length), 24);
        }

        public void VisitDataRef(DataRef field)
        {
            // Go to length offset
            SeekToOffset(field.Offset);

            // Read length
            field.Length = _reader.ReadInt32(); 

            // Skip 2 unknown int32's
            _reader.ReadBlock(0x08);

            // Read the memory address
            field.DataAddress = _reader.ReadUInt32();

            // Check if memory address is valid
            uint metaStartAddr = _cache.Info.MetaBase.AsAddress();
            uint metaEndAddr = metaStartAddr + _cache.Info.MetaSize;
            if (field.Length > 0 && field.DataAddress >= metaStartAddr && field.DataAddress + field.Length <= metaEndAddr)
            {
                // Go to position
                _reader.SeekTo(_cache.MetaPointerConverter.PointerToOffset(field.DataAddress));

                // Read Data
                byte[] data = _reader.ReadBlock(field.Length);

                // Convert to hex string
                field.Value = ExtryzeDLL.Util.FunctionHelpers.BytesToHexLines(data, 24);
            }
        }

        public void VisitTagRef(TagRefData field)
        {
            if (field.WithClass)
                SeekToOffset(field.Offset + (0x04 * 3));  // Skip class ID + two uint32 unknowns
            else
                SeekToOffset(field.Offset);

            DatumIndex index = DatumIndex.ReadFrom(_reader);
            if (index.IsValid && index.Index < field.Tags.Entries.Count)
            {
                TagEntry tag = field.Tags.Entries[index.Index];
                field.Class = tag.ParentClass;
                field.Value = tag;
            }
            else
            {
                field.Class = null;
                field.Value = null;
            }
        }

        public void VisitFloat32(Float32Data field)
        {
            SeekToOffset(field.Offset);
            field.Value = _reader.ReadFloat();
        }

        public void VisitVector(VectorData field)
        {
            SeekToOffset(field.Offset);
            field.X = _reader.ReadFloat();
            field.Y = _reader.ReadFloat();
            field.Z = _reader.ReadFloat();
        }

	    public void VisitDegree(DegreeData field)
	    {
		    SeekToOffset(field.Offset);
		    field.Radian = _reader.ReadFloat();
	    }

	    public void VisitReflexive(ReflexiveData field)
        {
            SeekToOffset(field.Offset);
            int length = _reader.ReadInt32();
            uint address = _reader.ReadUInt32();

            // Make sure the address looks valid
            uint metaStartAddr = _cache.Info.MetaBase.AsAddress();
            uint metaEndAddr = metaStartAddr + _cache.Info.MetaSize;
            if (address < metaStartAddr || address + length * field.EntrySize > metaEndAddr)
                length = 0;

            field.Length = length;
            if (address != field.FirstEntryAddress)
                field.FirstEntryAddress = address;
        }

        public void VisitReflexiveEntry(WrappedReflexiveEntry field)
        {
        }

        /// <summary>
        /// Opens the file for reading and sets _reader to the stream. Must be done before any I/O operations are performed.
        /// </summary>
        /// <returns>false if the file was already open.</returns>
        private bool OpenReader()
        {
            if (_reader == null)
            {
                _reader = new EndianReader(_streamManager.OpenRead(), Endian.BigEndian);
                return true;
            }
            return false;
        }

        private void CloseReader()
        {
            if (_reader != null)
            {
                _reader.Close();
                _reader = null;
            }
        }

        private void SeekToOffset(uint offset)
        {
            _reader.SeekTo(_baseOffset + offset);
        }
    }
}
