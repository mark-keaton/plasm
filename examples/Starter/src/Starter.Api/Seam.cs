// What the seam is told (Plasm.CSharp; Seam/ holds what it writes, after a
// build, and seam.lock the contract): the F# type the API calls, what C#
// calls its records and cases where the F# name is not the one wanted, and
// the request bodies, which its JSON context lists with the views. The C#
// compiler checks every typeof and nameof here.
using Plasm.CSharp;
using Starter;

// BookingData: Seam/BookingData.g.cs, the members of Starter.Data.BookingData
// over views.
[assembly: SeamData(typeof(Starter.Data.BookingData))]

// A booking's view leaves out its own id and its equipment's, which C# never needs.
[assembly: SeamView(typeof(Booking), Omit = [nameof(Booking.Id), nameof(Booking.Equipment)])]

// The JSON says bookable and withdrawn: {"kind":"withdrawn","reason":...,"since":...}.
[assembly: SeamCase(typeof(Condition), nameof(Condition.Available), Name = "Bookable")]
[assembly: SeamCase(typeof(Condition), nameof(Condition.OutOfService), Name = "Withdrawn")]

// The request bodies (Program.cs). A body left out would be SEAM030 at build.
[assembly: SeamJson(typeof(NewEquipment), typeof(NewBooking), typeof(OutOfService), typeof(InService))]
