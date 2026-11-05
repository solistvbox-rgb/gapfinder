using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace GapFinder.Scanning
{
    public class LibraryInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>"tvshows", "movies", "mixed" ... or null (mixed content).</summary>
        public string CollectionType { get; set; }
        public BaseItem Folder { get; set; }
    }

    public static class LibraryHelper
    {
        public static bool CanContainSeries(string collectionType) =>
            string.IsNullOrEmpty(collectionType)
            || string.Equals(collectionType, "tvshows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collectionType, "mixed", StringComparison.OrdinalIgnoreCase);

        public static bool CanContainMovies(string collectionType) =>
            string.IsNullOrEmpty(collectionType)
            || string.Equals(collectionType, "movies", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collectionType, "mixed", StringComparison.OrdinalIgnoreCase);

        /// <summary>Any real item of this type from the library, or null.</summary>
        public static BaseItem FirstItem(ILibraryManager libraryManager, LibraryInfo library, string itemType) =>
            libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { itemType },
                Parent = library.Folder,
                Recursive = true,
                IsVirtualItem = false,
                Limit = 1
            }).FirstOrDefault();

        /// <summary>All libraries that can contain series or movies.</summary>
        public static List<LibraryInfo> GetLibraries(ILibraryManager libraryManager)
        {
            var result = new List<LibraryInfo>();
            foreach (var vf in libraryManager.GetVirtualFolders())
            {
                if (!CanContainSeries(vf.CollectionType) && !CanContainMovies(vf.CollectionType))
                {
                    continue;
                }
                var folder = string.IsNullOrEmpty(vf.ItemId) ? null : libraryManager.GetItemById(vf.ItemId);
                if (folder == null)
                {
                    continue;
                }
                result.Add(new LibraryInfo { Id = vf.ItemId, Name = vf.Name, CollectionType = vf.CollectionType, Folder = folder });
            }
            return result.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Selected libraries; an empty selection means all.</summary>
        public static List<LibraryInfo> ResolveLibraries(ILibraryManager libraryManager, string[] libraryIds)
        {
            var all = GetLibraries(libraryManager);
            if (libraryIds == null || libraryIds.Length == 0)
            {
                return all;
            }
            var wanted = new HashSet<string>(libraryIds, StringComparer.OrdinalIgnoreCase);
            return all.Where(l => wanted.Contains(l.Id)).ToList();
        }
    }
}
