using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Capability
{
    /// <summary>What the probe proposes. Left at null, the lists come from <see cref="CapabilityCatalog"/>.</summary>
    public sealed class ProbeOptions
    {
        /// <summary>PS3.8: presentation context IDs are odd numbers from 1 to 255.</summary>
        public const int ContextLimit = 128;

        public IReadOnlyList<DicomUID> SopClasses { get; set; }
        public IReadOnlyList<DicomTransferSyntax> TransferSyntaxes { get; set; }
        /// <summary>Lower it for an SCP that cannot take many contexts in one request. At least 2, since the C-ECHO takes one.</summary>
        public int MaxContextsPerAssociation { get; set; } = ContextLimit;
        /// <summary>Also ask about role selection and extended negotiation, on an association of its own.</summary>
        public bool NegotiationFeatures { get; set; } = true;
    }

    /// <summary>One presentation context the probe proposes besides the C-ECHO's: one abstract syntax with exactly one transfer syntax.</summary>
    public sealed class ProbeContext
    {
        public DicomUID SopClass { get; set; }
        public DicomTransferSyntax Syntax { get; set; }
        /// <summary>Propose the SCP role for us. Only ever set on one context per abstract syntax, as PS3.7 allows one role item per SOP class.</summary>
        public bool ProposeScpRole { get; set; }
    }

    /// <summary>What one association proposes, apart from the C-ECHO context that fo-dicom adds first.</summary>
    public sealed class ProbeAssociationPlan
    {
        /// <summary>From 1.</summary>
        public int Index { get; set; }
        /// <summary>True for an association that asks about roles and extended negotiation and fills no cell.</summary>
        public bool IsFeatureProbe { get; set; }
        public List<ProbeContext> Contexts { get; } = new List<ProbeContext>();
        public List<DicomExtendedNegotiation> ExtendedNegotiations { get; } = new List<DicomExtendedNegotiation>();
    }

    /// <summary>The whole probe, split into associations that respect the context limit.</summary>
    public sealed class ProbePlan
    {
        /// <summary>The context every association has, because fo-dicom needs a request to open an association.</summary>
        public static readonly DicomUID EchoSopClass = DicomUID.Verification;
        public static readonly DicomTransferSyntax EchoSyntax = DicomTransferSyntax.ImplicitVRLittleEndian;

        // PS3.4 C.5.1: relational queries, date-time matching, fuzzy semantic matching and timezone adjustment.
        private static readonly byte[] FindInformation = { 1, 1, 1, 1 };
        // PS3.4 C.5.2 and C.5.3: relational retrieval.
        private static readonly byte[] RetrieveInformation = { 1 };

        private ProbePlan(IReadOnlyList<DicomUID> sopClasses, IReadOnlyList<DicomTransferSyntax> syntaxes)
        {
            SopClasses = sopClasses;
            TransferSyntaxes = syntaxes;
        }

        public IReadOnlyList<DicomUID> SopClasses { get; }
        public IReadOnlyList<DicomTransferSyntax> TransferSyntaxes { get; }
        public List<ProbeAssociationPlan> Associations { get; } = new List<ProbeAssociationPlan>();

        /// <summary>Whether the matrix has the combination that the C-ECHO's own context answers.</summary>
        public bool CoversEchoContext => SopClasses.Contains(EchoSopClass) && TransferSyntaxes.Contains(EchoSyntax);

        public static ProbePlan Create(ProbeOptions options)
        {
            options = options ?? new ProbeOptions();
            if (options.MaxContextsPerAssociation < 2 || options.MaxContextsPerAssociation > ProbeOptions.ContextLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"The contexts per association must be between 2 and {ProbeOptions.ContextLimit}.");
            }

            var sopClasses = (options.SopClasses ?? CapabilityCatalog.SopClasses).Distinct().ToList();
            var syntaxes = (options.TransferSyntaxes ?? CapabilityCatalog.TransferSyntaxes.Select(s => s.Syntax).ToList()).Distinct().ToList();
            if (sopClasses.Count == 0 || syntaxes.Count == 0) throw new ArgumentException("The probe needs at least one SOP class and one transfer syntax.", nameof(options));

            var plan = new ProbePlan(sopClasses, syntaxes);
            var perAssociation = options.MaxContextsPerAssociation - 1;

            // fo-dicom silently drops a context identical to one it already has, so the C-ECHO's own context must not be proposed again.
            var cells = sopClasses
                .SelectMany(sop => syntaxes.Select(syntax => new ProbeContext { SopClass = sop, Syntax = syntax }))
                .Where(c => !IsEchoContext(c))
                .ToList();

            plan.AddAssociations(cells, perAssociation, false);
            if (plan.Associations.Count == 0) plan.Associations.Add(new ProbeAssociationPlan { Index = 1 });
            if (options.NegotiationFeatures) plan.AddFeatureAssociations(perAssociation);
            return plan;
        }

        private static bool IsEchoContext(ProbeContext context) => context.SopClass == EchoSopClass && context.Syntax == EchoSyntax;

        private void AddAssociations(IReadOnlyList<ProbeContext> contexts, int perAssociation, bool featureProbe)
        {
            for (var start = 0; start < contexts.Count; start += perAssociation)
            {
                var association = new ProbeAssociationPlan { Index = Associations.Count + 1, IsFeatureProbe = featureProbe };
                association.Contexts.AddRange(contexts.Skip(start).Take(perAssociation));
                Associations.Add(association);
            }
        }

        private void AddFeatureAssociations(int perAssociation)
        {
            var contexts = new List<ProbeContext>();
            foreach (var sop in SopClasses)
            {
                var isStorage = sop.StorageCategory != DicomStorageCategory.None;
                if (isStorage || InformationFor(sop) != null)
                {
                    // Implicit VR is the one syntax every SCP must support, so the item is answered whatever the matrix says.
                    contexts.Add(new ProbeContext { SopClass = sop, Syntax = DicomTransferSyntax.ImplicitVRLittleEndian, ProposeScpRole = isStorage });
                }
            }

            var firstFeatureIndex = Associations.Count;
            AddAssociations(contexts, perAssociation, true);

            foreach (var association in Associations.Skip(firstFeatureIndex))
            {
                foreach (var context in association.Contexts)
                {
                    var information = InformationFor(context.SopClass);
                    if (information != null) association.ExtendedNegotiations.Add(new DicomExtendedNegotiation(context.SopClass, new DicomServiceApplicationInfo(information)));
                }
            }
        }

        private static byte[] InformationFor(DicomUID sop)
        {
            if (CapabilityCatalog.QueryRetrieveFind.Contains(sop)) return FindInformation;
            return CapabilityCatalog.QueryRetrieveMoveAndGet.Contains(sop) ? RetrieveInformation : null;
        }
    }
}
