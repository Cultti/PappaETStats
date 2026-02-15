using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

public sealed class Player
{
    [Key]
    [MaxLength(64)]
    public required string Guid { get; set; }

    public double Mu { get; set; }

    public double Sigma { get; set; }
}
