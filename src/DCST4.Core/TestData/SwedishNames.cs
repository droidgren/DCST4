using System;
using System.Linq;

namespace DCST4.Core.TestData
{
    /// <summary>
    /// Invented patient names: random combinations of common Swedish first names and surnames, in DICOM
    /// Surname^Firstname form. A share of them contain å, ä or ö so that character set handling gets exercised.
    /// </summary>
    public static class SwedishNames
    {
        /// <summary>Share of generated names that contain å, ä or ö.</summary>
        public const double SwedishCharacterShare = 0.40;

        private static readonly string[] MalePlain = { "Erik", "Lars", "Karl", "Anders", "Johan", "Per", "Nils", "Mikael", "Jan", "Hans", "Peter", "Olof", "Gustav", "Fredrik", "Daniel", "Oskar", "Henrik", "Viktor", "Emil", "Axel" };
        private static readonly string[] MaleSwedish = { "Åke", "Göran", "Håkan", "Björn", "Jörgen", "Örjan", "Östen", "Måns", "Börje", "Sören", "Pär", "Kåre" };
        private static readonly string[] FemalePlain = { "Anna", "Maria", "Karin", "Eva", "Kerstin", "Lena", "Emma", "Sara", "Elin", "Ida", "Linnea", "Sofia", "Johanna", "Hanna", "Elsa", "Ingrid", "Astrid", "Alice", "Maja", "Julia" };
        private static readonly string[] FemaleSwedish = { "Åsa", "Åse", "Märta", "Mårta", "Märit", "Göta", "Görel", "Sölve" };
        private static readonly string[] SurnamePlain = { "Andersson", "Johansson", "Karlsson", "Nilsson", "Eriksson", "Larsson", "Olsson", "Persson", "Svensson", "Gustafsson", "Pettersson", "Jonsson", "Jansson", "Hansson", "Bengtsson", "Lindberg", "Berg", "Holm", "Nyberg", "Sandberg" };
        private static readonly string[] SurnameSwedish = { "Åberg", "Åkesson", "Åström", "Ängström", "Öberg", "Öhman", "Östlund", "Sjöberg", "Sjögren", "Björk", "Bäckström", "Hägglund", "Lindström", "Bergström", "Nordström", "Söderberg", "Håkansson", "Göransson", "Jönsson", "Näslund", "Löfgren" };

        /// <summary>Returns a random name. <paramref name="sex"/> is "M", "F", or anything else for either.</summary>
        public static string Pick(Random random, string sex)
        {
            var male = sex == "M" || (sex != "F" && random.Next(2) == 0);
            var plainFirst = male ? MalePlain : FemalePlain;
            var swedishFirst = male ? MaleSwedish : FemaleSwedish;

            if (random.NextDouble() >= SwedishCharacterShare)
            {
                return Combine(random, SurnamePlain, plainFirst);
            }

            switch (random.Next(3))
            {
                case 0: return Combine(random, SurnameSwedish, plainFirst);
                case 1: return Combine(random, SurnamePlain, swedishFirst);
                default: return Combine(random, SurnameSwedish, swedishFirst);
            }
        }

        public static bool ContainsSwedishCharacter(string text) => text.Any(c => "åäöÅÄÖ".IndexOf(c) >= 0);

        private static string Combine(Random random, string[] surnames, string[] firstNames) =>
            surnames[random.Next(surnames.Length)] + "^" + firstNames[random.Next(firstNames.Length)];
    }
}
