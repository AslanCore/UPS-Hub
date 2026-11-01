using System.Text;
using UpsHub.Core.Update;

// Release helper for UPS Hub (used by .github/workflows/release.yml and locally).
//   keygen <secret-key-file> <public-key-file>   new Ed25519 key; the secret file is created 0600 and never printed
//   sums <out-file> <file>...                    SHA256SUMS.txt for the given files
//   sign <file> (--key-file PATH | --key-env NAME) [--comment TEXT]   writes <file>.minisig
//   verify <file> <public-key-file|key>          checks <file>.minisig
static int Usage()
{
    Console.Error.WriteLine("usage: upshub-release keygen SECRET PUBLIC | sums OUT FILE... | sign FILE (--key-file P | --key-env NAME) [--comment T] | verify FILE PUBKEY");
    return 2;
}

if (args.Length == 0) return Usage();
try
{
    switch (args[0])
    {
        case "keygen" when args.Length == 3:
        {
            if (File.Exists(args[1])) { Console.Error.WriteLine($"{args[1]} already exists; not overwriting."); return 1; }
            var k = Minisign.SecretKey.Generate();
            var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var f = new StreamWriter(new FileStream(args[1], opts))) f.Write(k.ToFileText());
            File.WriteAllText(args[2], Minisign.PublicKeyFileText(k.Public));
            Console.WriteLine($"Key id {k.Public.KeyIdHex}. Public key written to {args[2]}:");
            Console.WriteLine(k.Public.Encoded);
            return 0;
        }
        case "sums" when args.Length >= 3:
        {
            var lines = new List<(string, string)>();
            foreach (var f in args[2..]) lines.Add((Path.GetFileName(f), await Checksums.Sha256Async(f)));
            File.WriteAllText(args[1], Checksums.Format(lines), new UTF8Encoding(false));
            Console.Write(File.ReadAllText(args[1]));
            return 0;
        }
        case "sign" when args.Length >= 4:
        {
            string? keyText = null, comment = null;
            for (int i = 2; i < args.Length - 1; i++)
            {
                if (args[i] == "--key-file") keyText = File.ReadAllText(args[++i]);
                else if (args[i] == "--key-env") keyText = Environment.GetEnvironmentVariable(args[++i]);
                else if (args[i] == "--comment") comment = args[++i];
            }
            if (string.IsNullOrWhiteSpace(keyText)) { Console.Error.WriteLine("No signing key (empty file or environment variable)."); return 1; }
            var key = Minisign.SecretKey.Parse(keyText);
            var data = File.ReadAllBytes(args[1]);
            comment ??= $"timestamp:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\tfile:{Path.GetFileName(args[1])}\thashed";
            File.WriteAllText(args[1] + ".minisig", Minisign.Sign(key, data, comment));
            if (!Minisign.Verify(key.Public, data, File.ReadAllText(args[1] + ".minisig"), out var err)) throw new Exception("self-check failed: " + err);
            Console.WriteLine($"Signed {args[1]} with key {key.Public.KeyIdHex}.");
            return 0;
        }
        case "verify" when args.Length == 3:
        {
            var pub = Minisign.ParsePublicKey(File.Exists(args[2]) ? File.ReadAllText(args[2]) : args[2]);
            var ok = Minisign.Verify(pub, File.ReadAllBytes(args[1]), File.ReadAllText(args[1] + ".minisig"), out var err);
            Console.WriteLine(ok ? $"OK: {args[1]} is signed by {pub.KeyIdHex}." : "FAILED: " + err);
            return ok ? 0 : 1;
        }
        default: return Usage();
    }
}
catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
