namespace Trovami.Core.Models;

public enum Theme
{
    Dark,
    Light,
    System,
}

public enum EmailSecurity
{
    Auto,
    SslOnConnect,
    StartTls,
    None,
}

// Only picks a location's icon. Stored by name, so the order can change.
public enum LocationKind
{
    House,
    Apartment,
    Floor,
    Room,
    Cellar,
    Garage,
    Attic,
    Closet,
    Cabinet,
    Shelf,
    Rack,
    Drawer,
    Box,
    Bag,
    Other,
}

public enum TagKind
{
    Nfc,
    Qr,
}

public enum AiProvider
{
    None,
    Claude,
    OpenAi,
}

// Who gave an item its name. A name typed by a person is never overwritten by the AI.
public enum NameSource
{
    None,
    Person,
    Ai,
}

// Where an item stands with AI naming.
public enum AiNaming
{
    // It has a name, or no photo to name it from.
    NotNeeded,

    // Waiting for the photo or for the queue.
    Pending,

    // Tried and gave up (no key, or the call failed); named by hand.
    Failed,
}
