﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Blamite.Blam;
using Blamite.Flexibility.Settings;
using Blamite.IO;
using Blamite.Util;

namespace BlockAlignmentScanner
{
	class Program
	{
		static void Main(string[] args)
		{
			if (args.Length != 3)
			{
				Console.WriteLine("Usage: BlockAlignmentScanner <map dir> <in plugin dir> <out plugin dir>");
				return;
			}

			var mapDir = args[0];
			var inDir = args[1];
			var outDir = args[2];

			Console.WriteLine("Loading plugins...");
			var pluginsByClass = new Dictionary<string, XDocument>();
			foreach (var pluginPath in Directory.EnumerateFiles(inDir, "*.xml"))
			{
				Console.WriteLine("- {0}", pluginPath);
				var document = XDocument.Load(pluginPath);
				var className = Path.GetFileNameWithoutExtension(pluginPath);
				pluginsByClass[className] = document;
			}

			Console.WriteLine("Loading engine database...");
			var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			var dbPath = Path.Combine(exeDir, "Formats", "Engines.xml");
			var db = XMLEngineDatabaseLoader.LoadDatabase(dbPath);

			Console.WriteLine("Processing maps...");
			var alignsByElem = new Dictionary<XElement, int>();
			foreach (var mapPath in Directory.EnumerateFiles(mapDir, "*.map"))
			{
				Console.WriteLine("- {0}", Path.GetFileName(mapPath));
				using (var reader = new EndianReader(File.OpenRead(mapPath), Endian.BigEndian))
				{
					var map = CacheFileLoader.LoadCacheFile(reader, db);
					foreach (var tag in map.Tags)
					{
						if (tag == null || tag.Class == null || tag.MetaLocation == null)
							continue;

						// Get the plugin for the tag
						var className = CharConstant.ToString(tag.Class.Magic);
						XDocument plugin;
						if (!pluginsByClass.TryGetValue(className, out plugin))
							continue;

						// Process it
						var baseOffset = tag.MetaLocation.AsOffset();
						var baseElement = plugin.Element("plugin");
						DetectAlignment(map, reader, baseOffset, baseElement, alignsByElem);
					}
				}
			}

			Console.WriteLine("Adjusting plugins...");
			foreach (var align in alignsByElem)
			{
				if (align.Value != 4)
				{
					Console.WriteLine("- \"{0}\" -> align 0x{1:X}", align.Key.Attribute("name").Value, align.Value);
					align.Key.SetAttributeValue(XName.Get("align"), "0x" + align.Value.ToString("X"));
				}
			}

			if (!Directory.Exists(outDir))
				Directory.CreateDirectory(outDir);

			Console.WriteLine("Saving plugins...");
			foreach (var plugin in pluginsByClass)
			{
				var outPath = Path.Combine(outDir, plugin.Key + ".xml");
				Console.WriteLine("- {0}", outPath);

				var settings = new XmlWriterSettings();
				settings.Indent = true;
				settings.IndentChars = "\t";
				using (var writer = XmlWriter.Create(outPath, settings))
					plugin.Value.Save(writer);
			}
		}

		static void DetectAlignment(ICacheFile cacheFile, IReader reader, int baseOffset, XElement baseElement, Dictionary<XElement, int> alignsByElem)
		{
			// Loop through all tag blocks and data references
			foreach (var elem in baseElement.Elements())
			{
				var isTagBlock = (elem.Name.LocalName == "reflexive");
				var isDataRef = (elem.Name.LocalName == "dataRef");
				if (!isTagBlock && !isDataRef)
					continue;

				// Read the address
				var offset = ParseInteger(elem.Attribute("offset").Value);
				var count = 0;
				if (isTagBlock)
				{
					reader.SeekTo(baseOffset + offset);
					count = reader.ReadInt32();
				}
				else
				{
					reader.SeekTo(baseOffset + offset + 0xC);
				}

				var addr = reader.ReadUInt32();
				if (addr == 0)
					continue;

				// Only update the alignment if it's less than the currently-guessed alignment
				int oldAlign;
				var newAlign = GetAlignment(addr);
				if (!alignsByElem.TryGetValue(elem, out oldAlign) || newAlign < oldAlign)
					alignsByElem[elem] = newAlign;

				// If it's a tag block, then recurse into it
				if (isTagBlock)
				{
					var entrySize = ParseInteger(elem.Attribute("entrySize").Value);
					var blockBaseOffset = cacheFile.MetaArea.PointerToOffset(addr);
					for (var i = 0; i < count; i++)
						DetectAlignment(cacheFile, reader, blockBaseOffset + i * entrySize, elem, alignsByElem);
				}
			}
		}

		static int GetAlignment(uint val)
		{
			if (val == 0)
				return 32;

			var align = 1;
			while ((val & align) == 0)
				align <<= 1;
			
			return align;
		}

		static int ParseInteger(string str)
		{
			if (str.StartsWith("0x"))
				return int.Parse(str.Substring(2), NumberStyles.HexNumber);
			return int.Parse(str);
		}
	}
}
