using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class WayfinderStandaloneBuilder
{
    private const string ScenePath = "Assets/Wayfinder/Scenes/WayfinderRiver.unity";
    private const string MacBuildPath = "Builds/Wayfinder.app";

    [MenuItem("Wayfinder/Build macOS Game")]
    public static void BuildMacGame()
    {
        if (!File.Exists(ScenePath))
        {
            throw new InvalidOperationException("Cannot build Wayfinder: scene is missing at " + ScenePath);
        }

        Directory.CreateDirectory("Builds");
        PlayerSettings.productName = "Wayfinder - Patience Opens the Path";
        PlayerSettings.companyName = "Wayfinder Hackathon Team";
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = MacBuildPath,
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.CleanBuildCache
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Wayfinder macOS build failed: {report.summary.result}, " +
                $"{report.summary.totalErrors} errors, {report.summary.totalWarnings} warnings.");
        }

        Debug.Log(
            $"WAYFINDER GAME BUILD PASSED: {MacBuildPath} " +
            $"({report.summary.totalSize / (1024f * 1024f):0.0} MB)");
    }
}
