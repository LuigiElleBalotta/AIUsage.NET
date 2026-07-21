using AIUsage.Core.Stores;
using AIUsage.Core.Tests.TestHelpers;
using Xunit;

namespace AIUsage.Core.Tests.Stores;

public class ProviderAccountIDTests
{
    [Fact]
    public void Make_IsDeterministic_ForSameIdentityKey()
    {
        var a = ProviderAccountID.Make("claude", "uuid-1|org-1");
        var b = ProviderAccountID.Make("claude", "uuid-1|org-1");
        Assert.Equal(a, b);
        Assert.StartsWith("claude@", a);
        Assert.Equal("claude@".Length + 8, a.Length);
    }

    [Fact]
    public void Make_IsCaseInsensitive_OnIdentityKey()
    {
        var lower = ProviderAccountID.Make("claude", "uuid-1|org-1");
        var upper = ProviderAccountID.Make("claude", "UUID-1|ORG-1");
        Assert.Equal(lower, upper);
    }

    [Fact]
    public void Make_DiffersForDifferentIdentityKeys()
    {
        var a = ProviderAccountID.Make("claude", "uuid-1|org-1");
        var b = ProviderAccountID.Make("claude", "uuid-2|org-1");
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("claude@ab12cd34", "claude")]
    [InlineData("claude", "claude")]
    public void Family_ExtractsPrefixBeforeAt(string cardId, string expected)
    {
        Assert.Equal(expected, ProviderAccountID.Family(cardId));
    }

    [Theory]
    [InlineData("claude@ab12cd34", true)]
    [InlineData("claude", false)]
    public void IsAccountCard_DetectsAtSign(string cardId, bool expected)
    {
        Assert.Equal(expected, ProviderAccountID.IsAccountCard(cardId));
    }
}

public class ProviderAccountRecordTests
{
    [Fact]
    public void DerivedDisplayName_ReturnsCapitalizedFamily_ForBareCard()
    {
        var record = new ProviderAccountRecord { Id = "claude", Family = "claude", IdentityKey = "uuid" };
        Assert.Equal("Claude", record.DerivedDisplayName);
    }

    [Fact]
    public void DerivedDisplayName_UsesOrgFromParenthesizedLabel_ForAccountCard()
    {
        var record = new ProviderAccountRecord
        {
            Id = "claude@ab12cd34",
            Family = "claude",
            IdentityKey = "uuid",
            Label = "person@example.com (Acme Corp)"
        };
        Assert.Equal("Claude — Acme Corp", record.DerivedDisplayName);
    }

    [Fact]
    public void DerivedDisplayName_UsesFullLabel_WhenNoParenthesizedOrg()
    {
        var record = new ProviderAccountRecord
        {
            Id = "claude@ab12cd34",
            Family = "claude",
            IdentityKey = "uuid",
            Label = "person@example.com"
        };
        Assert.Equal("Claude — person@example.com", record.DerivedDisplayName);
    }

    [Fact]
    public void DerivedDisplayName_FallsBackToId_WhenNoLabel()
    {
        var record = new ProviderAccountRecord { Id = "claude@ab12cd34", Family = "claude", IdentityKey = "uuid" };
        Assert.Equal("claude@ab12cd34", record.DerivedDisplayName);
    }

    [Fact]
    public void ResolvedDisplayName_PrefersCustomLabel()
    {
        var record = new ProviderAccountRecord
        {
            Id = "claude@ab12cd34",
            Family = "claude",
            IdentityKey = "uuid",
            Label = "person@example.com (Acme Corp)",
            CustomLabel = "Work Claude"
        };
        Assert.Equal("Work Claude", record.ResolvedDisplayName);
    }
}

public class ProviderAccountsStoreTests
{
    private static ProviderAccountsStore.AccountObservation DefaultHomeObservation(string identityKey, string? label = null) => new()
    {
        Family = "claude",
        IdentityKey = identityKey,
        Label = label,
        Sources = new List<ProviderAccountSource>
        {
            new() { Kind = ProviderAccountSource.SourceKind.DefaultHome, Anchor = "~/.claude", HoldsDefaultSource = true }
        }
    };

    private static ProviderAccountsStore.AccountObservation ConfigDirObservation(string identityKey, string anchor, string? label = null) => new()
    {
        Family = "claude",
        IdentityKey = identityKey,
        Label = label,
        Sources = new List<ProviderAccountSource>
        {
            new() { Kind = ProviderAccountSource.SourceKind.ConfigDir, Anchor = anchor, HoldsDefaultSource = false, KeychainLiteral = anchor }
        }
    };

    [Fact]
    public void Reconcile_FirstAccountAtDefaultHome_GetsBareFamilyId()
    {
        var store = new ProviderAccountsStore(new InMemorySettingsStore());
        var records = store.Reconcile(new List<ProviderAccountsStore.AccountObservation> { DefaultHomeObservation("uuid-1") });

        var record = Assert.Single(records);
        Assert.Equal("claude", record.Id);
    }

    [Fact]
    public void Reconcile_ExtraConfigDirAccount_MintsHashedId()
    {
        var store = new ProviderAccountsStore(new InMemorySettingsStore());
        store.Reconcile(new List<ProviderAccountsStore.AccountObservation> { DefaultHomeObservation("uuid-1") });
        var records = store.Reconcile(new List<ProviderAccountsStore.AccountObservation>
        {
            ConfigDirObservation("uuid-2", "/home/user/.claude-work")
        });

        var extra = records.First(r => r.IdentityKey == "uuid-2");
        Assert.Equal(ProviderAccountID.Make("claude", "uuid-2"), extra.Id);
        Assert.NotEqual("claude", extra.Id);
    }

    [Fact]
    public void Reconcile_IsIdempotent_ForUnchangedObservation()
    {
        var settings = new InMemorySettingsStore();
        var store = new ProviderAccountsStore(settings);
        store.Reconcile(new List<ProviderAccountsStore.AccountObservation> { DefaultHomeObservation("uuid-1", "a@b.com") });
        var persistedAfterFirst = settings.GetString(ProviderAccountsStore.StorageKey);

        store.Reconcile(new List<ProviderAccountsStore.AccountObservation> { DefaultHomeObservation("uuid-1", "a@b.com") });
        var persistedAfterSecond = settings.GetString(ProviderAccountsStore.StorageKey);

        Assert.Equal(persistedAfterFirst, persistedAfterSecond);
    }

    [Fact]
    public void Reconcile_SwappingDefaultAccount_StripsDefaultBadgeFromPreviousHolder()
    {
        var store = new ProviderAccountsStore(new InMemorySettingsStore());
        store.Reconcile(new List<ProviderAccountsStore.AccountObservation> { DefaultHomeObservation("uuid-1") });
        // uuid-2 now occupies the default home (e.g. the user switched accounts via `claude` login).
        store.Reconcile(new List<ProviderAccountsStore.AccountObservation> { DefaultHomeObservation("uuid-2") });

        var previous = store.Records.First(r => r.IdentityKey == "uuid-1");
        Assert.DoesNotContain(previous.Sources, s => s.HoldsDefaultSource);
    }

    [Fact]
    public void Reconcile_PersistsAcrossStoreInstances()
    {
        var settings = new InMemorySettingsStore();
        var first = new ProviderAccountsStore(settings);
        first.Reconcile(new List<ProviderAccountsStore.AccountObservation> { DefaultHomeObservation("uuid-1", "a@b.com") });

        var second = new ProviderAccountsStore(settings);
        Assert.Single(second.Records);
        Assert.Equal("uuid-1", second.Records[0].IdentityKey);
    }

    [Fact]
    public void Rename_SetsCustomLabel_AndPersists()
    {
        var settings = new InMemorySettingsStore();
        var store = new ProviderAccountsStore(settings);
        store.Reconcile(new List<ProviderAccountsStore.AccountObservation> { DefaultHomeObservation("uuid-1") });

        store.Rename("claude", "My Personal Claude");

        Assert.Equal("My Personal Claude", store.ResolvedDisplayName("claude"));
        var reloaded = new ProviderAccountsStore(settings);
        Assert.Equal("My Personal Claude", reloaded.ResolvedDisplayName("claude"));
    }

    [Fact]
    public void Rename_ToBlank_ClearsBackToDerivedName()
    {
        var store = new ProviderAccountsStore(new InMemorySettingsStore());
        store.Reconcile(new List<ProviderAccountsStore.AccountObservation> { DefaultHomeObservation("uuid-1") });
        store.Rename("claude", "Custom");
        store.Rename("claude", "  ");

        Assert.Equal("Claude", store.ResolvedDisplayName("claude"));
    }

    [Fact]
    public void ResolvedDisplayName_ReturnsNull_ForUnknownCard()
    {
        var store = new ProviderAccountsStore(new InMemorySettingsStore());
        Assert.Null(store.ResolvedDisplayName("codex"));
    }
}
