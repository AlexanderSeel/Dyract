using System.Text;
using Dyract.Client;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Session;
using Dyract.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dyract.Tests;

public sealed class IdentitySignerAbstractionTests
{
    private const string SessionId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task NonExportingSigner_SupportsInvitationDirectoryCapabilitySignalingAndSessionHandshake()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var httpClient = factory.CreateClient();
        using var alice = NonExportingSigner.Generate();
        using var bob = NonExportingSigner.Generate();
        var directory = new DirectoryClient(httpClient);
        var signaling = new PeerSignalingClient(httpClient);

        var invitation = ContactInvitationFactory.Create(alice);
        Assert.True(ContactInvitationCodec.TryDecode(invitation, out var decodedInvitation, out var invitationError), invitationError);
        Assert.NotNull(decodedInvitation);
        Assert.Equal(alice.PeerId.Value, decodedInvitation.PeerId);
        Assert.Equal(alice.ExportPublicKey(), Convert.FromBase64String(decodedInvitation.PublicKey));

        var aliceRegistration = await directory.RegisterAsync(alice);
        var bobRegistration = await directory.RegisterAsync(bob);
        Assert.Equal(alice.PeerId.Value, aliceRegistration.PeerId);
        Assert.Equal(bob.PeerId.Value, bobRegistration.PeerId);

        var capability = ContactCapabilityFactory.Create(
            bob,
            alice.PeerId.Value,
            TimeSpan.FromMinutes(10));
        Assert.Equal(bob.PeerId.Value, capability.IssuerPeerId);
        Assert.Equal(alice.PeerId.Value, capability.GranteePeerId);

        var resolve = await directory.ResolveAsync(alice, bob.PeerId.Value, capability);
        Assert.Equal(bob.PeerId.Value, resolve.PeerId);
        Assert.False(resolve.IsReachable);

        var signal = await signaling.SendAsync(
            alice,
            bob.PeerId.Value,
            capability,
            PeerSignalingClient.CreateSessionId(),
            PeerSignalTypes.Offer,
            "{\"type\":\"offer\"}");
        var pending = await signaling.FetchAsync(bob);
        Assert.Contains(pending.Signals, item => item.SignalId == signal.SignalId);
        await signaling.AckAsync(bob, [signal.SignalId]);
        Assert.DoesNotContain((await signaling.FetchAsync(bob)).Signals, item => item.SignalId == signal.SignalId);

        using var initiator = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bob.ExportPublicKey(),
            SessionId);
        var responder = AuthenticatedSessionResponder.Accept(
            bob,
            alice.PeerId,
            alice.ExportPublicKey(),
            initiator.HelloPacket,
            SessionId);
        using var responderKeys = responder.Keys;
        using var initiatorKeys = initiator.Complete(responder.ResponsePacket);
        using var sender = new AuthenticatedSessionCipher(initiatorKeys);
        using var receiver = new AuthenticatedSessionCipher(responderKeys);
        var plaintext = Encoding.UTF8.GetBytes("non-exporting signer session");

        Assert.Equal(plaintext, receiver.Decrypt(sender.Encrypt(plaintext)));
    }

    [Fact]
    public void SignerContract_ContainsNoPrivateKeyExportOperation()
    {
        var memberNames = typeof(IPeerIdentitySigner)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains("get_PeerId", memberNames);
        Assert.Contains(nameof(IPeerIdentitySigner.ExportPublicKey), memberNames);
        Assert.Contains(nameof(IPeerIdentitySigner.Sign), memberNames);
        Assert.DoesNotContain(memberNames, name => name.Contains("Private", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(memberNames, name => name.Contains("Pkcs8", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class NonExportingSigner : IPeerIdentitySigner, IDisposable
    {
        private readonly PeerIdentity _inner;

        private NonExportingSigner(PeerIdentity inner)
        {
            _inner = inner;
        }

        public PeerId PeerId => _inner.PeerId;

        public static NonExportingSigner Generate()
            => new(PeerIdentity.Generate());

        public byte[] ExportPublicKey()
            => _inner.ExportPublicKey();

        public byte[] Sign(ReadOnlySpan<byte> payload)
            => _inner.Sign(payload);

        public void Dispose()
            => _inner.Dispose();
    }
}
