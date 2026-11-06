using DCST4.Core.Generation;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace DCST4.Models
{
    /// <summary>A tag added under Settings, as saved in the settings file. A tag on the common list has no Value: it generates its own.</summary>
    public class AdditionalTagSetting
    {
        public string Tag { get; set; }
        public string Vr { get; set; }
        public string PrivateCreator { get; set; }
        public string Value { get; set; }

        public static AdditionalTagSetting From(AdditionalTag tag) => new AdditionalTagSetting
        {
            Tag = tag.Number,
            Vr = tag.VrCode,
            PrivateCreator = tag.PrivateCreator,
            Value = tag.DefaultValue,
        };

        /// <summary>The tag, or null if the saved number cannot be read.</summary>
        public AdditionalTag ToTag()
        {
            if (!AdditionalTagParser.TryParseNumber(Tag, out var group, out var element)) return null;
            return new AdditionalTag(group, element, Vr, PrivateCreator, Value);
        }

        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(List<AdditionalTagSetting>));

        public static string Serialize(IEnumerable<AdditionalTagSetting> tags)
        {
            using (var writer = new StringWriter())
            {
                Serializer.Serialize(writer, tags.ToList());
                return writer.ToString();
            }
        }

        /// <summary>Reads tags from XML. Throws on invalid XML; an empty string gives an empty list.</summary>
        public static List<AdditionalTagSetting> Deserialize(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return new List<AdditionalTagSetting>();
            using (var reader = new StringReader(xml))
            {
                return (List<AdditionalTagSetting>)Serializer.Deserialize(reader) ?? new List<AdditionalTagSetting>();
            }
        }
    }
}
