// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Sources;

namespace Kuestenlogik.Bowire.Catalogue.Kubernetes.Tests;

/// <summary>
/// What the Settings → Catalogue providers dialog sends for this package,
/// and whether it arrives (#309).
/// </summary>
/// <remarks>
/// <para>
/// Core does not reference this package, so an override is matched onto
/// <see cref="BowireKubernetesCatalogueOptions"/> by reflection: by assembly
/// name prefix, by provider id, by options class name, by property name and
/// by constructor shape. Five string-matched couplings with nothing checking
/// any of them, and a silent fallback to the parameterless constructor
/// whenever one misses — which loads the provider on its own defaults and
/// discards everything the operator typed.
/// </para>
/// <para>
/// That fallback was being taken. The resolver constructor's third parameter
/// is <c>IKubernetesEnvironment</c>, the seam could produce no value for it,
/// and a missing value reads as "this constructor will not do" — so the
/// override never took effect and the dialog appeared to do nothing.
/// </para>
/// </remarks>
public sealed class KubernetesCatalogueOverrideSeamTests
{
    public KubernetesCatalogueOverrideSeamTests()
        // The seam scans AppDomain.CurrentDomain.GetAssemblies(), and a
        // referenced assembly is not loaded until something touches it.
        => _ = typeof(KubernetesCatalogueProvider);

    [Theory]
    [InlineData("kubernetes")]
    [InlineData("KUBERNETES")]
    public void The_Provider_Is_Found_By_Its_Id(string id)
    {
        var provider = BowireCatalogueOverrideStore.BuildProvider(
            new BowireCatalogueOverride { Provider = id });

        Assert.IsType<KubernetesCatalogueProvider>(provider);
    }

    [Fact]
    public void Selected_Without_Options_It_Still_Loads()
    {
        // The dialog can post a bare provider id; the provider then runs on
        // its own in-cluster discovery rather than not loading.
        var provider = BowireCatalogueOverrideStore.BuildProvider(
            new BowireCatalogueOverride { Provider = "kubernetes", Kubernetes = null });

        Assert.IsType<KubernetesCatalogueProvider>(provider);
    }

    [Fact]
    public void What_The_Operator_Typed_Reaches_The_Provider()
    {
        var provider = BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride
        {
            Provider = "kubernetes",
            Kubernetes = new BowireKubernetesCatalogueOverrideOptions
            {
                ApiServerUrl = "https://k8s.internal:6443",
                Namespace = "harbor",
                LabelSelector = "bowire.io/catalogue=true",
                SkipTlsVerification = true,
            },
        });

        var options = ResolvedOptions(provider!);

        Assert.Equal("https://k8s.internal:6443", options.ApiServerUrl);
        Assert.Equal("harbor", options.Namespace);
        Assert.Equal("bowire.io/catalogue=true", options.LabelSelector);
        Assert.True(options.SkipTlsVerification);
    }

    [Fact]
    public void The_Dependencies_The_Provider_Needs_Are_Still_Supplied()
    {
        // Carrying the options across must not cost the provider the
        // filesystem and env-var seam its parameterless ctor wires up — it
        // reads that unguarded, so a null would only surface on the first
        // fetch, as a crash rather than a dropped setting.
        var provider = BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride
        {
            Provider = "kubernetes",
            Kubernetes = new BowireKubernetesCatalogueOverrideOptions { Namespace = "harbor" },
        });

        foreach (var field in provider!.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            Assert.NotNull(field.GetValue(provider));
        }
    }

    [Fact]
    public void A_Field_Left_Blank_Keeps_The_Provider_Default()
    {
        // The dialog posts every field of its form, most of them empty. An
        // empty box means "unset", not "set to the empty string" — which
        // would overwrite what the provider discovers for itself in-cluster.
        var untouched = new BowireKubernetesCatalogueOptions();

        var provider = BowireCatalogueOverrideStore.BuildProvider(new BowireCatalogueOverride
        {
            Provider = "kubernetes",
            Kubernetes = new BowireKubernetesCatalogueOverrideOptions
            {
                Namespace = "harbor",
                ApiServerUrl = "",
                Token = null,
            },
        });

        var options = ResolvedOptions(provider!);

        Assert.Equal("harbor", options.Namespace);
        Assert.Equal(untouched.ApiServerUrl, options.ApiServerUrl);
        Assert.Equal(untouched.Token, options.Token);
    }

    /// <summary>
    /// The options the provider will resolve, read off the resolver delegate
    /// the seam handed its constructor. Direct, because this provider has no
    /// offline path that would show them and the alternative is a call to a
    /// real API server.
    /// </summary>
    private static BowireKubernetesCatalogueOptions ResolvedOptions(object provider)
    {
        var field = provider.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.FieldType == typeof(Func<BowireKubernetesCatalogueOptions>));
        Assert.NotNull(field);

        return ((Func<BowireKubernetesCatalogueOptions>)field.GetValue(provider)!)();
    }
}
