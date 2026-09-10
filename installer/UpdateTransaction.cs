using System;
using System.Collections.Generic;
using System.IO;
using Jvdp.Reliability;

namespace Jvdp.LightDarkroomInstaller
{
    // One durable manifest describes the entire previous installation. Recovery
    // is repeatable after failure at any point, including a process interruption.
    internal sealed class UpdateTransaction
    {
        private readonly string root, backup, manifest;
        internal UpdateTransaction(string directory)
        {
            root = Path.GetFullPath(directory);
            backup = Path.Combine(root, "previous-version");
            manifest = Path.Combine(backup, "manifest.txt");
        }
        internal bool Pending { get { return File.Exists(Path.Combine(root, "update-in-progress.txt")); } }
        internal void Begin(IEnumerable<string> files)
        {
            if (Pending) throw new InvalidOperationException("Een vorige update moet eerst worden hersteld.");
            Directory.CreateDirectory(backup);
            List<string> entries = new List<string>();
            foreach (string name in files)
            {
                ValidateName(name);
                string source = Path.Combine(root, name), destination = Path.Combine(backup, name);
                bool exists = File.Exists(source);
                if (exists) File.Copy(source, destination, true);
                else if (File.Exists(destination)) File.Delete(destination);
                entries.Add((exists ? "1|" : "0|") + name);
            }
            ReliableFiles.Write(manifest, String.Join("\n", entries.ToArray()));
            ReliableFiles.Write(Path.Combine(root, "update-in-progress.txt"), DateTime.UtcNow.ToString("o"));
        }
        internal void Rollback()
        {
            if (!Pending) return;
            if (!File.Exists(manifest)) throw new InvalidDataException("De herstelgegevens ontbreken.");
            foreach (string entry in File.ReadAllLines(manifest))
            {
                if (entry.Length < 3 || entry[1] != '|' || (entry[0] != '0' && entry[0] != '1'))
                    throw new InvalidDataException("Ongeldig herstelmanifest.");
                string name = entry.Substring(2); ValidateName(name);
                string target = Path.Combine(root, name);
                if (entry[0] == '1')
                {
                    string temporary = target + ".restore";
                    File.Copy(Path.Combine(backup, name), temporary, true);
                    if (File.Exists(target)) File.Replace(temporary, target, null);
                    else File.Move(temporary, target);
                }
                else if (File.Exists(target)) File.Delete(target);
            }
            Commit();
        }
        internal void Commit()
        {
            string pending = Path.Combine(root, "update-in-progress.txt");
            if (File.Exists(pending)) File.Delete(pending);
        }
        private static void ValidateName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name == "." || name == ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Ongeldige bestandsnaam in update.");
        }
    }
}
