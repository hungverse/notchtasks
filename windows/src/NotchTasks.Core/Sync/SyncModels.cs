namespace NotchTasks.Core.Sync;

/// The group the Mac or this PC shares with the phones: g names it on the server, k seals its boxes, uid and refresh are the server session.
public sealed record SyncGroup(string G, string K, string Uid, string Refresh);

/// The pairing JSON the QR code carries: v is the format (1), g and k as in SyncGroup, n the name of the computer that shows it.
public sealed record PairingCode(int V, string G, string K, string N);
