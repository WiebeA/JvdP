using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using Jvdp.Reliability;

namespace Jvdp.LightDarkroomOverlay
{
    internal sealed class BoothSettings
    {
        public int Schema = 1;
        public string BoothId = Guid.NewGuid().ToString("N");
        public string Name = Environment.MachineName;
        public string Camera = "";
        public int Revision = 1;
        public int DarkRaw = 0;
        public int BrightRaw = 4095;
        public int Margin = 2;
        public int MaximumIso = 25600;
        public bool AllowUntestedDarkroom;
        public string TestedDarkroomVersion = "";
        public int Stability = 60;
        public bool CustomMapping;
        public List<IsoBand> Bands = new List<IsoBand>();
        public bool CustomCover;
        public string CoverTitle = "Please wait!";
        public string CoverMessage = "I'm fine-tuning the lighting\r\nso you look absolutely amazing!";

        internal void Validate()
        {
            if (Schema != 1 || String.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Camera == null || Camera.Length > 100 ||
                !Guid.TryParseExact(BoothId, "N", out ignoredGuid) || Revision < 1)
                throw new InvalidDataException("Onbekend of ongeldig boothprofiel.");
            if (DarkRaw < 0 || DarkRaw > 4095 || BrightRaw < 0 || BrightRaw > 4095 ||
                Math.Abs(BrightRaw - DarkRaw) < 100 || Margin < 0 || Margin > 10 ||
                Stability < 5 || Stability > 300 || Array.IndexOf(OverlayForm.SupportedIsoValues, MaximumIso) < 0)
                throw new InvalidDataException("Controleer kalibratie (minimaal 100 ADC verschil), marge, wachttijd en maximale ISO.");
            if (Bands == null || Bands.Count < 1 || Bands.Count > 8)
                throw new InvalidDataException("Een profiel heeft 1 tot 8 aansluitende bereiken nodig.");
            int previous = -1;
            foreach (IsoBand band in Bands)
            {
                if (band == null || band.MaximumLight <= previous || band.MaximumLight > 100 ||
                    Array.IndexOf(OverlayForm.SupportedIsoValues, band.Iso) < 0)
                    throw new InvalidDataException("Ongeldige lichtgrens of ISO in het profiel.");
                previous = band.MaximumLight;
            }
            if (previous != 100 || CoverTitle == null || CoverMessage == null ||
                CoverTitle.Length > 500 || CoverMessage.Length > 2000 ||
                (CustomCover && (String.IsNullOrWhiteSpace(CoverTitle) || String.IsNullOrWhiteSpace(CoverMessage))))
                throw new InvalidDataException("Het profiel moet tot 100 lopen en geldige schermtekst bevatten.");
        }
        private Guid ignoredGuid;
        internal string Serialize() { Validate(); return new JavaScriptSerializer().Serialize(this); }
        internal BoothSettings Clone() { return Parse(Serialize()); }
        internal static BoothSettings Parse(string content)
        {
            if (content.Length > 65536) throw new InvalidDataException("Profielbestand is te groot.");
            BoothSettings value = new JavaScriptSerializer().Deserialize<BoothSettings>(content);
            if (value == null) throw new InvalidDataException("Leeg profielbestand.");
            value.Validate(); return value;
        }
        internal void Save(string path) { ReliableFiles.Write(path, Serialize()); }
    }
}
