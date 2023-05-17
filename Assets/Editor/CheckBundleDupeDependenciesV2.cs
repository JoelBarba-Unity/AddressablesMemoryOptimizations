/// This AnalyzeRule is based on the built-in rule CheckBundleDupeDependencies
/// This rule finds assets in Addressables that will be duplicated across multiple AssetBundles
/// Instead of placing all problematic assets in a shared Group, this rule results in fewer AssetBundles
/// being created by placing assets with the same AssetBundle parents into the same label and AssetBundle
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Pipeline;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets.Build.AnalyzeRules;

class CheckBundleDupeDependenciesV2 : BundleRuleBase
{
    #region Types
    private struct DuplicateResult
    {
        public AddressableAssetGroup Group;

        public string DuplicatedFile;
        
        public string AssetPath;
        
        public GUID DuplicatedGroupGuid;
    }
    #endregion

    #region Properties
    // Return true because we have added an automated way of fixing these problems with the FixIssues() function
    public override bool CanFix
    {
        get
        {
            return true;
        }
    }

    // The name that appears in the Editor UI
    public override string ruleName
    {
        get
        {
            return "Check Duplicate Bundle Dependencies V2";
        }
    }
    #endregion

    #region Fields
    [NonSerialized]
    internal readonly Dictionary<string, Dictionary<string, List<string>>> m_AllIssues = new Dictionary<string, Dictionary<string, List<string>>>();

    [SerializeField]
    internal Dictionary<List<string>, List<string>> duplicateAssetsAndParents = new Dictionary<List<string>, List<string>>();
    #endregion

    #region Methods
    // The function that is called when the user clicks "Analyze Selected Rules" in the Analyze window
    public override List<AnalyzeResult> RefreshAnalysis(AddressableAssetSettings settings)
    {
        // Clear values of the scene.
        ClearAnalysis();

        // Check for duplicate dependencies.
        return CheckForDuplicateDependencies(settings);
    }

    private List<AnalyzeResult> CheckForDuplicateDependencies(AddressableAssetSettings settings)
    {
        // Create a container to store all our AnalyzeResults
        List<AnalyzeResult> retVal = new List<AnalyzeResult>();

        // Request that the user save the scene if it is not yet saved.
        // If the opened scene was not saved by the user, then:
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            // Log an error.
            Debug.LogError("Cannot run Analyze with unsaved scenes");
            
            // Add the error as an analyze result, which will populate the Analyze Rules UI with a visualization of the error.
            retVal.Add(new AnalyzeResult { resultName = ruleName + "Cannot run Analyze with unsaved scenes" });

            // Return the analyze rule results.
            return retVal;
        }

        // Otherwise, the opened scene was saved by the user.

        // Internal Addressables function that populates m_AllBundleInputDefs with what all our bundles will look like
        CalculateInputDefinitions(settings);

        // If there are no bundles to build, then:
        if (m_AllBundleInputDefs.Count <= 0)
        {
            // Return the analyze rule results.
            return retVal;
        }

        // Otherwise, there are bundles to build.

        // Get the build context using the addressables settings.
        AddressableAssetsBuildContext context = GetBuildContext(settings);

        ReturnCode exitCode = RefreshBuild(context);

        // If the exit code was not equal to a success, then the build refresh was a failure.
        // If the build refresh was a failure, then:
        if (exitCode < ReturnCode.Success)
        {
            // Log an error.
            Debug.LogError("Analyze build failed. " + exitCode);

            // Add the error as an analyze result, which will populate the Analyze Rules UI with a visualization of the error.
            retVal.Add(new AnalyzeResult { resultName = ruleName + "Analyze build failed. " + exitCode });

            // Return the analyze rule results.
            return retVal;
        }

        // Otherwise, the build refresh was a success.

        // A set of asset GUIDs associated with the list of AssetBundles that will contain the asset.
        // NOTE: If there is more than one AssetBundle, that means the asset is being duplicated in multiple assets.
        Dictionary<GUID, List<string>> implicitGuids = GetImplicitGuidToFilesMap();

        // Actually calculate the duplicates.
        IEnumerable<DuplicateResult> dupeResults = CalculateDuplicates(implicitGuids, context);

        // Populate m_AllIssues with the duplicates that were found.
        BuildImplicitDuplicatedAssetsSet(dupeResults);

        // Create analyze rule entries from the m_AllIssues entries, which is a flattened version of the m_AllIssues entries.
        retVal = (from issueGroup in m_AllIssues
                  from bundle in issueGroup.Value
                  from item in bundle.Value
                  select new AnalyzeResult
                  {
                      resultName = ruleName + kDelimiter +
                                           issueGroup.Key + kDelimiter +
                                           ConvertBundleName(bundle.Key, issueGroup.Key) + kDelimiter +
                                           item,
                      severity = MessageType.Warning
                  }).ToList();

        // If there are no entries, then:
        if (retVal.Count == 0)
        {
            // Add an entry that specifies that there were no errors.
            retVal.Add(noErrors);
        }

        // Return the result.
        return retVal;
    }

    private IEnumerable<DuplicateResult> CalculateDuplicates(Dictionary<GUID, List<string>> implicitGuids, AddressableAssetsBuildContext aaContext)
    {
        // Clear the duplicates asset entries.
        duplicateAssetsAndParents.Clear();

        // Get all guids that have more than one bundle referencing them

        // For every list of files associated with a GUID, make sure the files are distinct.
        // If there is more than one distinct AssetBundle file, then determine if the GUID is a valid entry in the asset database.
        // Collect the entries that match this criteria, since it is the list of GUIDs that
        // are duplicated by being packed in more than one AssetBundle.
        IEnumerable<KeyValuePair<GUID, List<string>>> validGuids =
            from dupeGuid in implicitGuids
            where dupeGuid.Value.Distinct().Count() > 1
            where IsValidPath(AssetDatabase.GUIDToAssetPath(dupeGuid.Key.ToString()))
            select dupeGuid;

        // Key = a set of bundle parents
        // Value = asset paths that share the same bundle parents
        // e.g. <{"bundle1", "bundle2"} , {"Assets/Sword_D.tif", "Assets/Sword_N.tif"}>

        // For every valid duplicated asset, perform the following:
        foreach (KeyValuePair<GUID, List<string>> entry in validGuids)
        {
            // Make AssetBundles distinct.
            List<string> distinctParents = new List<string>();

            // For every AssetBundle in the list, perform the following:
            foreach (var parent in entry.Value)
            {
                // If the distinct set contains the entry, then:
                if (distinctParents.Contains(parent))
                {
                    // Do nothing.
                    continue;
                }

                // Otherwise, the entry is unique.
                
                // Add it to the list of distinct entries.
                distinctParents.Add(parent);
            }

            // Get the asset path associated with the GUID.
            string assetPath = AssetDatabase.GUIDToAssetPath(entry.Key.ToString());

            // A value indicating that the asset path was added to an existing set of AssetBundles.
            bool found = false;

            // For every AssetBundle set, perform the following:
            foreach (var bundleParentSetup in duplicateAssetsAndParents.Keys)
            {
                // If the distinct parents of this asset entry match a key that is already present, then:
                if (Enumerable.SequenceEqual(bundleParentSetup, distinctParents))
                {
                    // Add this asset to this dictionary entry.
                    duplicateAssetsAndParents[bundleParentSetup].Add(assetPath);

                    // Mark the dictionary as found.
                    found = true;

                    // Break out of the iteration loop.
                    break;
                }
            }

            // If an entry was not found, then:
            if (!found)
            {
                // We failed to find an existing set of matching bundle parents.
                // Add a new entry where the list of AssetBundles are the keys, and the asset path is the value.
                duplicateAssetsAndParents.Add(distinctParents, new List<string>() { assetPath });
            }
        }

        // Construct an instance of a duplicate result 
        return
            from guidToFile in validGuids
            from file in guidToFile.Value

            // Get the files that belong to those guids
            let fileToBundle = m_ExtractData.WriteData.FileToBundle[file]

            // Get the bundles that belong to those files
            let bundleToGroup = aaContext.bundleToAssetGroup[fileToBundle]

            // Get the asset groups that belong to those bundles
            let selectedGroup = aaContext.Settings.FindGroup(findGroup => findGroup != null && findGroup.Guid == bundleToGroup)

            select new DuplicateResult
            {
                Group = selectedGroup,
                DuplicatedFile = file,
                AssetPath = AssetDatabase.GUIDToAssetPath(guidToFile.Key.ToString()),
                DuplicatedGroupGuid = guidToFile.Key
            };
    }

    private void BuildImplicitDuplicatedAssetsSet(IEnumerable<DuplicateResult> dupeResults)
    {
        // For every duplicate result in the list, perform the following:
        foreach (var dupeResult in dupeResults)
        {
            // Attempt to get the value associated with the group name.
            // If no value exists, then:
            if (!m_AllIssues.TryGetValue(dupeResult.Group.Name, out Dictionary<string, List<string>> groupData))
            {
                // Create a new entry and cache it locally.
                groupData = new Dictionary<string, List<string>>();

                // Add the data to the AllIssues container which is shown in the Analyze window.
                m_AllIssues.Add(dupeResult.Group.Name, groupData);
            }

            // Get the group name.
            string groupName = m_ExtractData.WriteData.FileToBundle[dupeResult.DuplicatedFile];
            // Attempt to get the value associated with the group name.
            // If no value exists, then:
            if (!groupData.TryGetValue(groupName, out List<string> assets))
            {
                // Create a new entry and cache it locally.
                assets = new List<string>();

                // Add the assets to the container, which will populate the entry in m_AllIssues.
                groupData.Add(groupName, assets);
            }

            // Populate the duplicated asset in the asset list for group data.
            assets.Add(dupeResult.AssetPath);
        }
    }

    // The function that is called when the user clicks "Fix Issues" in the Analyze window
    public override void FixIssues(AddressableAssetSettings settings)
    {
        // If the duplicate dictionary is null or empty, then we have no duplicate data.
        // If we have no duplicate data, then:
        if (duplicateAssetsAndParents == null || duplicateAssetsAndParents.Count == 0)
        {
            // Check for duplicates again.
            CheckForDuplicateDependencies(settings);
        }

        // If the duplicate dictionary is empty, then we did not find duplicates.
        // If we did not find duplicates, then:
        if (duplicateAssetsAndParents.Count == 0)
        {
            // Do nothing else.
            return;
        }

        // Otherwise, we did find duplicates.

        // Setup a new Addressables Group to store all our duplicate assets
        const string desiredGroupName = "Duplicate Assets Sorted By Label";
        
        // Determine if there is already a group in the Addressable settings with that name.
        AddressableAssetGroup group = settings.FindGroup(desiredGroupName);

        // If there is no group with that name, then:
        if (group == null)
        {
            // Create an Addressables group with that name and default group schemas.
            group = settings.CreateGroup(desiredGroupName, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));

            // Get the schema instance from a group.
            var bundleSchema = group.GetSchema<BundledAssetGroupSchema>();

            // Set the schema's bundle mode to pack-by-label.
            bundleSchema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogetherByLabel;
        }

        // Begin displaying a progress bar for when the deduplications are fixed. 
        EditorUtility.DisplayProgressBar("Setting up De-Duplication Group...", "", 0f / duplicateAssetsAndParents.Count);

        // There will be one AssetBundle per duplicate list.
        int bundleNumber = 1;
        
        // For every duplicate asset in the list of assets, perform the following:
        foreach (var entry in duplicateAssetsAndParents)
        {
            // Update the progress bar.
            EditorUtility.DisplayProgressBar("Setting up De-Duplication Group...", "Creating Label Group", ((float)bundleNumber) / duplicateAssetsAndParents.Count);
            
            // Create a new Label based on the bundle number.
            string desiredLabelName = "Bundle" + bundleNumber;

            // Create a list of entries to add.
            var entriesToAdd = new List<AddressableAssetEntry>();
            
            // For every duplicate entry, perform the following:
            foreach (string assetPath in entry.Value)
            {
                // Get the GUID for the main asset at the path.
                string guid = AssetDatabase.AssetPathToGUID(assetPath);

                // Request the asset entry associated with the GUID, which can be lazily instantiated.
                AddressableAssetEntry assetEntry = settings.CreateOrMoveEntry(guid, group, false, false);

                // Put each duplicate in the shared Group.
                entriesToAdd.Add(assetEntry);
            }

            // Add the new label to the Addressables settings.
            settings.AddLabel(desiredLabelName);

            // Set the label for this set of duplicates.
            // NOTE: At build time, Pack by label will make it so that the duplicates are packed together.
            SetLabelValueForEntries(settings, entriesToAdd, desiredLabelName);

            // Increase the AssetBundle number.
            bundleNumber++;
        }

        // Mark the settings as dirty, and post the event as a modification that was done as a batch.
        settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
    }

    // Helper function for adding labels to Addressable assets
    private void SetLabelValueForEntries(AddressableAssetSettings settings, List<AddressableAssetEntry> entries, string label, bool postEvent = true)
    {
        // For every asset entry in the list of entries, perform the following:
        foreach (var e in entries)
        {
            // Add the label to the asset entry. Avoid posting an event until we mark the settings as dirty.
            e.SetLabel(label, true, false);
        }

        // Mark the settings as dirty, and post the event as a modified asset entry.
        settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entries, postEvent, true);
    }

    // The function that is run when the user clicks "Clear Selected Rules" in the Analyze window
    public override void ClearAnalysis()
    {
        // Clear all the issues.
        m_AllIssues.Clear();

        // Clear the asset duplicates and the parents.
        duplicateAssetsAndParents.Clear();
        
        // Include the base functionality.
        base.ClearAnalysis();
    }
    #endregion
}

/// <summary>
/// Boilerplate to add our rule to the AnalyzeSystem's list of rules.
/// </summary>
[InitializeOnLoad]
class RegisterCheckBundleDupeDependenciesV2
{
    static RegisterCheckBundleDupeDependenciesV2()
    {
        // Register a the CheckBundleDupeDependenciesV2 analyze rule with the analyze system.
        AnalyzeSystem.RegisterNewRule<CheckBundleDupeDependenciesV2>();
    }
}