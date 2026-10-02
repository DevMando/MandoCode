namespace MandoCode.Services;

/// <summary>
/// The callsign pool for agent naming — street-energy handles in the spirit of breaking
/// cyphers, phone phreaks, and construct crews, without naming any real person or property:
/// distinctive real-world handles are riffed rather than copied (the "Morphy" rule), and
/// everything else is ordinary words that just sound like they earned a spot in a cypher.
///
/// Selection is a shuffled deck: no name repeats until the whole pool has been dealt, then it
/// reshuffles. The deck is app-session state — a restart reshuffles. Names already worn by an
/// open tab are skipped; in the (theoretical) case that every callsign is simultaneously in
/// use, naming falls back to <see cref="AgentNaming.NextFreeName"/> numbers.
/// </summary>
public sealed class AgentCallsigns
{
    private readonly Random Rng = new();
    private readonly List<string> Deck = new();

    public string Next(IEnumerable<string?> takenTitles)
    {
        var titles = takenTitles.ToList();
        var taken = new HashSet<string>(titles.Where(t => !string.IsNullOrEmpty(t))!,
            StringComparer.OrdinalIgnoreCase);

        // Deal until a name lands that no open tab is wearing — reshuffling at most once per
        // call, so a pool with nothing free degrades to numbers instead of spinning.
        var reshuffled = false;
        while (true)
        {
            if (Deck.Count == 0)
            {
                if (reshuffled) return AgentNaming.NextFreeName(titles);
                Deck.AddRange(Pool);
                for (var i = Deck.Count - 1; i > 0; i--)   // Fisher–Yates
                {
                    var j = Rng.Next(i + 1);
                    (Deck[i], Deck[j]) = (Deck[j], Deck[i]);
                }
                reshuffled = true;
            }

            var name = Deck[^1];
            Deck.RemoveAt(Deck.Count - 1);
            if (!taken.Contains(name)) return name;
        }
    }

    /// <summary>Forgets the current deal so the next draw starts a fresh shuffled cycle.
    /// Exists for tests, which need cycle behavior to be observable from a known state.</summary>
    public void ResetDeck() => Deck.Clear();

    public static readonly IReadOnlyList<string> Pool = new[]
    {
        "Morphy", "Neo", "Trin", "Cypher", "Oracle", "Tank", "Dozer", "Mouse",
        "Switch", "Link", "Seraph", "Keysmith", "Construct", "Sentinel", "Nebu", "Merov",
        "Niobe", "Sati", "Smitty", "Rabbit", "Deja", "Anomaly", "Redshift",
        // Hand-picked additions:
        "Phteven", "Mandox", "Sequoia", "Tule", "Merrill", "Parker", "Rey", "Warlock",
        "Kaweah", "Wichita", "KC", "Dodge", "Doppler", "Nimbus", "Cirus",
        "Dorth", "Chiki", "Lulu", "Cripto", "Mandeezy", "Finity", "Runner", "ExJay",
        "Wrangler", "Zonik", "M-117", "Loggic", "Blazor", "Michelle", "Han", "Twister",
        "Cuamatzi",
        "Condor", "Crunch", "Phreak", "Acid", "Burn", "Crash", "Override", "Zed",
        "Razor", "Blade", "Falkon", "Root", "Sudo", "Kernel",
        "Shell", "Grep", "Proxy", "Hex", "Null", "Void",
        "Segfault", "Packet", "Socket", "Ping", "Tracer", "Payload", "Cache",
        "Regex", "Lambda", "Quine", "Enigma", "Morse", "Opcode", "Byte",
        "Chip", "Circuit", "Diode", "Neon", "Laser", "Photon", "Prism",
        "Firewall", "Mainframe", "Codec", "Modem", "Terminal",
        "Cloud", "Storm", "Wing", "Freeze", "Flare", "Halo", "Cyclone",
        "Vortex", "Torque", "Kinetik", "Jetik", "Physix", "Rukus", "Havok", "Kaos",
        "Vertigo", "Spinz", "Mills", "Boogie", "Flava", "Breaker", "Ghost", "Banshee", 
        "Funk", "Groove", "Riddim", "Tempo", "Beatz", "Blaze", "Ember",
        "Inferno", "Frost", "Glacier", "Tundra", "Quake", "Tremor", "Rumble", "Thunder",
        "Bolt", "Volt", "Surge", "Static", "Spark", "Flux", "Pulse", "Eclipse",
        "Comet", "Meteor", "Nova", "Quasar", "Pulsar", "Nebula", "Orbit", "Zenith",
        "Apex", "Vertex", "Kryptik", "Mystik", "Majik", "Logik", "Tekniq", "Uniq",
        "Freq", "Sonik", "Kosmik", "Atomik", "Elektrik", "Dynamik", "Klassik", "Fanatik",
        "Mekanik", "Organik", "Volkanik", "Galaktik", "Robotik", "Poetik", "Akrobat", "Hypnotik",
        "Toxik", "Seismik", "Optik", "Grafik", "Frantik", "Drastik", "Tactik",
        "Drift", "Skid", "Dash", "Vault", "Flip",
        "Ollie", 
        "Sway",
        "Strobe", "Flash", 
        "Ray", "Beam", "Lumen", "Lux", "Aurora",
        "Titan", "Atlas", "Orion", "Vega", "Sirius", "Rigel", "Lyra", "Draco",
        "Nyx", "Erebus", "Chronos", "Hyperion", "Icarus", "Nemesis", "Janus", "Juno",
        "Ceres", "Vesta", "Io", "Callisto", "Europa", "Andromeda",
        "Phoenix", "Gryphon", "Hydra", "Kraken", "Wyvern", "Basilisk", "Chimera", "Sphinx",
        "Cobra", "Viper", "Mamba", "Python", "Adder", "Raptor", "Osprey", "Talon",
        "Panther", "Lynx", "Ocelot", "Cheetah", "Wolf", "Lobo", "Coyote", "Vixen",
        "Mantis", "Hornet", "Wasp", "Firefly", "Dragonfly",
        "Rhino", "Bison", "Grizzly", "Kodiak", "Husky", "Akita", "Dingo",
        "Mongoose", "Badger", "Serval", "Caracal", "Jackal", "Fennec",
        "Ronin", "Shogun", "Sensei", "Ninja", "Shinobi", "Kunai", "Shuriken", "Katana",
        "Sabre", "Rapier", "Dagger", "Kris", "Scimitar", "Falchion", "Cutlass", "Claymore",
        "Bishop", "Rook", "Gambit", "Checkm8", "Blitz", "Bullet",
        "Dice", "Domino", "Ace", "Deuce", "Wildcard", "Maverick",
        "Rebel", "Rogue", "Bandit", "Outlaw", "Renegade", "Drifter", "Nomad", "Vagabond",
        "Recon", "Stealth", "Decoy", "Smoke", "Mirage", "Cloak", "Veil", "Whisper",
        "Scout", "Ranger", "Warden", "Sentry", "Vanguard", "Bastion", "Citadel", "Aegis",
        "Paladin", "Ricochet", "Ballistix", "Zigzag", "Riddle", "Karma", "Mantra",
        "Zen", "Halcyon", "Ozone", "Headrush", "Adrenalin",
        "Specter", "Phantom", "Wraith", "Shade", "Shadow", "Eidolon", "Revenant", "Umbra",
        "Onyx", "Obsidian", "Jade", "Cobalt", "Crimson", "Scarlet", "Indigo", "Violet",
        "Slate", "Graphite", "Steel", "Iron", "Mercury", "Platinum",
        "Titanium", "Tungsten", "Granite", "Basalt", "Flint", "Quartz", "Topaz", "Garnet",
        "Argon", "Xenon", "Krypton", "Plasma", "Ion", "Isotope", "Quark",
        "Proton", "Neutron", "Electron", "Fusion", "Fission", "Reactor", "Dynamo", "Turbine",
        "Piston", "Throttle", "Clutch", "Nitro", "Turbo", "Redline", "Burnout", "Slipstream",
        "Rocket", "Thruster", "Igniter", "Apogee",
        "Radar", "Sonar", "Lidar", "Beacon", "Signal", "Uplink",
        "Relay", "Conduit", "Transistor", "Amp", "Waveform",
        "Echo", "Reverb", "Tremolo", "Crescendo", "Staccato",
        "Allegro", "Forte", "Presto", "Octave", "Cadence",
        "Scratch", "Fader", "Vinyl", "Needle", "Breakbeat", "Beatbox",
        "Remix", "Dub", "Bassline", "Subz", "Snare",
        "Monsoon", "Typhoon", "Sirocco", "Mistral", "Zephyr", "Gale", "Squall", "Tempest",
        "Avalanche", "Icicle", "Polaris", "Boreal", "Arctic", "Chill", "Coldsnap",
        "Solstice", "Equinox", "Midnight", "Dusk", "Dawn", "Nightfall", "Daybreak",
        "Horizon", "Meridian",
        "Canyon", "Mesa", "Dune", "Oasis", "Sahara", "Savanna", "Delta", "Ridge",
        "Summit", "Crag", "Bluff", "Cliff", "Fjord", "Reef",
        "Rapids", "Cascade", "Torrent", "Riptide", "Undertow", "Geyser", "Wake",
        "Stencil", "Tag", "Aerosol", "FatKap", "Wildstyle", "Throwie", "Burner",
        "Flexx", "Twista", "Spida", "Casper", "Primo", "Nollie", "Fakie",
        "Hurricane", "Tornado", "Loki", "Odin", "Freya", "Fenrir", "Valkyrie",
        "Zeus", "Hermes", "Apollo", "Artemis", "Athena", "Ares", "Helios", "Selene",
        "Wisp", "Cinder", "Ash",
        "Pixel", "Sprite", "Voxel", "Shader", "Raster", "Vector",
        "Render", "Framez", "Raycast", "Skybox", "Bloom",
    };
}
