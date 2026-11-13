using System.Collections;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;

namespace DCST4.Infrastructure
{
    /// <summary>
    /// A transfer syntax list grouped by codec family, as the lists show it under a heading per family. The items keep
    /// their order, which is the order of the families.
    /// </summary>
    public static class FamilyGroupedView
    {
        public const string FamilyProperty = "Family";

        public static ICollectionView Create(IEnumerable items)
        {
            var view = new ListCollectionView(items.Cast<object>().ToList());
            view.GroupDescriptions.Add(new PropertyGroupDescription(FamilyProperty));
            return view;
        }
    }
}
