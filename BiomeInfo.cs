namespace OriathHub.Plugins.Atlas
{
    using System.Numerics;

    /// <summary>
    ///     Runtime biome definition. The label comes from <c>EndgameMapBiomes.dat</c> in game
    ///     memory; the border colour and visibility default until the user overrides them.
    /// </summary>
    public sealed class BiomeInfo
    {
        /// <summary>Gets or sets the display label for this biome.</summary>
        public string? Label { get; set; }

        /// <summary>Gets or sets the RGBA border colour as a four-element float array.</summary>
        public float[]? BorderColor { get; set; }

        /// <summary>Gets or sets whether this biome's border is drawn.</summary>
        public bool Show { get; set; } = true;

        /// <summary>Gets the border colour as a <see cref="Vector4"/>.</summary>
        public Vector4 BdColor => this.BorderColor is { Length: 4 }
            ? new Vector4(this.BorderColor[0], this.BorderColor[1], this.BorderColor[2], this.BorderColor[3])
            : Vector4.One;
    }
}
