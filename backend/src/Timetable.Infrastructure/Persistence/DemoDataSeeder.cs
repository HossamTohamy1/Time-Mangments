using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Timetable.Application.Abstractions;
using Timetable.Application.Features.Curriculum;
using Timetable.Application.Features.Institutions;
using Timetable.Domain.Academic;
using Timetable.Domain.Common;
using Timetable.Domain.Configuration;
using Timetable.Domain.Organization;
using Timetable.Domain.Resources;
using Timetable.Domain.Scheduling;
using Timetable.Infrastructure.Identity;

namespace Timetable.Infrastructure.Persistence;

/// <summary>
/// Bilingual demo data: a sample university ("UNI") and a sample secondary school ("SEC"), plus demo users.
/// Everything is created through the same configuration services the UI uses (templates + curriculum).
/// </summary>
public sealed class DemoDataSeeder(
    AppDbContext db,
    ITenantContext tenant,
    InstitutionProvisioner provisioner,
    CurriculumSessionGenerator curriculum,
    UserManager<AppUser> users,
    ILogger<DemoDataSeeder> logger)
{
    public const string DemoPassword = "Demo#12345";
    public const string AdminEmail = "admin@demo.local";

    public async Task SeedAsync(CancellationToken ct)
    {
        using var bypass = tenant.Bypass();
        if (await db.Institutions.AnyAsync(ct)) return;
        logger.LogInformation("Seeding demo institutions and users");

        var admin = await EnsureUser(AdminEmail, "مدير النظام", "System Administrator", "en");
        var uni = (await provisioner.CreateAsync("UNI", "جامعة القاهرة التكنولوجية", "Cairo Tech University", "university", "ar", admin.Id, ct)).Value;
        var school = (await provisioner.CreateAsync("SEC", "مدرسة النور الثانوية", "Al-Nour Secondary School", "secondary", "ar", admin.Id, ct)).Value;
        admin.DefaultInstitutionId = uni.Id;
        await users.UpdateAsync(admin);

        var (uniTerm, ahmedId, groupY3A) = await SeedUniversity(uni.Id, ct);
        await SeedSchool(school.Id, ct);

        var scheduler = await EnsureUser("scheduler@demo.local", "منسق الجداول", "Timetable Scheduler", "ar");
        scheduler.DefaultInstitutionId = uni.Id;
        await users.UpdateAsync(scheduler);
        await Assign(scheduler.Id, uni.Id, "SCHEDULER", ct);
        await Assign(scheduler.Id, school.Id, "SCHEDULER", ct);

        var instructor = await EnsureUser("dr.ahmed@demo.local", "د. أحمد حسن", "Dr. Ahmed Hassan", "en");
        instructor.DefaultInstitutionId = uni.Id;
        instructor.InstructorId = ahmedId;
        await users.UpdateAsync(instructor);
        await Assign(instructor.Id, uni.Id, "INSTRUCTOR", ct);

        var student = await EnsureUser("student@demo.local", "طالب تجريبي", "Demo Student", "ar");
        student.DefaultInstitutionId = uni.Id;
        student.StudentGroupId = groupY3A;
        await users.UpdateAsync(student);
        await Assign(student.Id, uni.Id, "STUDENT", ct);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Demo data ready (term {Term}). Log in as {Admin} / {Password}", uniTerm, AdminEmail, DemoPassword);
    }

    private async Task<AppUser> EnsureUser(string email, string ar, string en, string lang)
    {
        var u = await users.FindByEmailAsync(email);
        if (u is not null) return u;
        u = new AppUser
        {
            UserName = email, Email = email, EmailConfirmed = true, DisplayNameAr = ar, DisplayNameEn = en,
            PreferredLanguage = lang, CreatedAt = DateTimeOffset.UtcNow,
        };
        var r = await users.CreateAsync(u, DemoPassword);
        if (!r.Succeeded) throw new DomainException("SEED_USER_FAILED", string.Join("; ", r.Errors.Select(e => e.Description)));
        return u;
    }

    private async Task Assign(Guid userId, Guid institutionId, string roleCode, CancellationToken ct)
    {
        var role = await db.AppRoles.FirstAsync(r => r.InstitutionId == institutionId && r.Code == roleCode, ct);
        db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = userId, InstitutionId = institutionId, RoleId = role.Id });
    }

    private async Task<T> Lookup<T>(DbSet<T> set, Guid inst, string code) where T : Domain.Lookups.LookupEntity =>
        await set.FirstAsync(x => x.InstitutionId == inst && x.Code == code);

    // ------------------------------------------------------------------ University
    private async Task<(Guid TermId, Guid AhmedId, Guid GroupY3A)> SeedUniversity(Guid inst, CancellationToken ct)
    {
        tenant.Set(inst);
        var faculty = await Lookup(db.OrgUnitTypes, inst, "FACULTY");
        var dept = await Lookup(db.OrgUnitTypes, inst, "DEPARTMENT");
        var program = await Lookup(db.OrgUnitTypes, inst, "PROGRAM");
        var year = await Lookup(db.OrgUnitTypes, inst, "YEAR");
        var cohort = await Lookup(db.GroupKinds, inst, "COHORT");
        var section = await Lookup(db.GroupKinds, inst, "SECTION");
        var labGroup = await Lookup(db.GroupKinds, inst, "LAB_GROUP");
        var hall = await Lookup(db.RoomTypes, inst, "HALL");
        var classroom = await Lookup(db.RoomTypes, inst, "CLASSROOM");
        var lab = await Lookup(db.RoomTypes, inst, "LAB");
        var auditorium = await Lookup(db.RoomTypes, inst, "AUDITORIUM");
        var doctor = await Lookup(db.InstructorTypes, inst, "DOCTOR");
        var ta = await Lookup(db.InstructorTypes, inst, "TA");
        var lecture = await Lookup(db.SessionTypes, inst, "LECTURE");
        var sec = await Lookup(db.SessionTypes, inst, "SECTION");
        var labType = await Lookup(db.SessionTypes, inst, "LAB");

        OrgUnit Unit(string code, string ar, string en, Guid type, Guid? parent)
        {
            var u = new OrgUnit { InstitutionId = inst, Code = code, NameAr = ar, NameEn = en, OrgUnitTypeId = type, ParentId = parent };
            db.OrgUnits.Add(u);
            return u;
        }
        var feng = Unit("FENG", "كلية الهندسة", "Faculty of Engineering", faculty.Id, null);
        var cs = Unit("CS", "قسم علوم الحاسب", "Computer Science Department", dept.Id, feng.Id);
        var csp = Unit("CSP", "برنامج علوم الحاسب", "Computer Science Program", program.Id, cs.Id);
        var y3 = Unit("CS-Y3", "الفرقة الثالثة", "Year 3", year.Id, csp.Id);
        var y4 = Unit("CS-Y4", "الفرقة الرابعة", "Year 4", year.Id, csp.Id);

        var bldB = new Building { InstitutionId = inst, Code = "B", NameAr = "المبنى ب", NameEn = "Building B", Zone = "Cluster B" };
        var bldC = new Building { InstitutionId = inst, Code = "C", NameAr = "المبنى ج", NameEn = "Building C", Zone = "North" };
        db.Buildings.AddRange(bldB, bldC);
        db.BuildingTravelTimes.Add(new BuildingTravelTime { InstitutionId = inst, FromBuildingId = bldB.Id, ToBuildingId = bldC.Id, Minutes = 10 });

        Room R(string code, string ar, string en, Guid type, int cap, Guid bld, params string[] eq)
        {
            var r = new Room { InstitutionId = inst, Code = code, NameAr = ar, NameEn = en, RoomTypeId = type, Capacity = cap, BuildingId = bld, Equipment = [.. eq] };
            db.Rooms.Add(r);
            return r;
        }
        R("H1", "مدرج 1", "Lecture Hall 1", hall.Id, 250, bldB.Id, "PROJECTOR");
        R("H2", "مدرج 2", "Lecture Hall 2", hall.Id, 180, bldB.Id, "PROJECTOR");
        R("H3", "مدرج 3", "Lecture Hall 3", hall.Id, 160, bldC.Id, "PROJECTOR");
        R("AUD-A", "القاعة الكبرى أ", "Auditorium A", auditorium.Id, 300, bldC.Id, "PROJECTOR");
        R("R201", "قاعة 201", "Room 201", classroom.Id, 80, bldB.Id);
        R("R204", "قاعة 204", "Room 204", classroom.Id, 80, bldB.Id, "SMARTBOARD");
        R("R105", "قاعة 105", "Room 105", classroom.Id, 80, bldC.Id);
        R("R106", "قاعة 106", "Room 106", classroom.Id, 80, bldC.Id);
        R("LAB2", "معمل 2", "Lab 2", lab.Id, 40, bldB.Id, "PC");
        R("LAB3", "معمل 3", "Lab 3", lab.Id, 45, bldB.Id, "PC", "PROJECTOR");
        R("LAB4", "معمل 4", "Lab 4", lab.Id, 40, bldC.Id, "PC");

        StudentGroup G(string code, string ar, string en, Guid kind, int count, OrgUnit unit, StudentGroup? parent = null)
        {
            var g = new StudentGroup { InstitutionId = inst, Code = code, NameAr = ar, NameEn = en, GroupKindId = kind, StudentCount = count, OrgUnitId = unit.Id, ParentGroupId = parent?.Id };
            db.StudentGroups.Add(g);
            return g;
        }
        StudentGroup? firstSection = null;
        foreach (var (unit, n, size) in new[] { (y3, "3", 150), (y4, "4", 120) })
        {
            var c = G($"CS-Y{n}", $"علوم الحاسب - الفرقة {n}", $"CS - Year {n}", cohort.Id, size, unit);
            foreach (var s in new[] { "A", "B" })
            {
                var sg = G($"CS-Y{n}-{s}", $"علوم الحاسب - فرقة {n} - مجموعة {s}", $"CS - Year {n} - Section {s}", section.Id, size / 2, unit, c);
                firstSection ??= sg;
                foreach (var l in new[] { "1", "2" })
                    G($"CS-Y{n}-{s}{l}", $"مجموعة معمل {s}{l} - فرقة {n}", $"Lab group {s}{l} - Year {n}", labGroup.Id, size / 4, unit, sg);
            }
        }

        Instructor I(string code, string ar, string en, Guid type, decimal maxWeek, decimal? maxDay = null)
        {
            var i = new Instructor { InstitutionId = inst, Code = code, NameAr = ar, NameEn = en, InstructorTypeId = type, MaxHoursPerWeek = maxWeek, MaxHoursPerDay = maxDay, Email = $"{code.ToLowerInvariant()}@uni.demo" };
            db.Instructors.Add(i);
            return i;
        }
        var ahmed = I("AHASSAN", "د. أحمد حسن", "Dr. Ahmed Hassan", doctor.Id, 18, 6);
        var sara = I("SNABIL", "د. سارة نبيل", "Dr. Sara Nabil", doctor.Id, 18, 6);
        var khaled = I("KZAKI", "د. خالد زكي", "Dr. Khaled Zaki", doctor.Id, 18, 6);
        var yasser = I("YFOUAD", "د. ياسر فؤاد", "Dr. Yasser Fouad", doctor.Id, 18, 6);
        var mona = I("MALI", "م. منى علي", "Eng. Mona Ali", ta.Id, 24);
        var omar = I("OTAHA", "م. عمر طه", "Eng. Omar Taha", ta.Id, 24);
        var karim = I("KADEL", "م. كريم عادل", "Eng. Karim Adel", ta.Id, 24);
        var dina = I("DFATHY", "م. دينا فتحي", "Eng. Dina Fathy", ta.Id, 24);
        var tarek = I("TNOUR", "م. طارق نور", "Eng. Tarek Nour", ta.Id, 24);

        Course C(string code, string ar, string en, string color, params string[] tags)
        {
            var c = new Course { InstitutionId = inst, Code = code, NameAr = ar, NameEn = en, Color = color, Tags = [.. tags], Credits = 3, OrgUnitId = csp.Id };
            db.Courses.Add(c);
            return c;
        }
        var cs201 = C("CS201", "هياكل البيانات والخوارزميات", "Data Structures & Algorithms", "#3b5bdb", "core");
        var cs204 = C("CS204", "نظم التشغيل", "Operating Systems", "#8b3fd9", "core");
        var cs210 = C("CS210", "الرياضيات المتقطعة", "Discrete Mathematics", "#0f9488", "math");
        var math201 = C("MATH201", "الجبر الخطي", "Linear Algebra", "#c2410c", "math");
        var cs305 = C("CS305", "قواعد البيانات المتقدمة", "Advanced Database Systems", "#15a35a", "core");
        var cs402 = C("CS402", "النظم الموزعة", "Distributed Systems", "#4338ca", "core");
        var cs311 = C("CS311", "هندسة البرمجيات", "Software Engineering", "#be185d", "core");
        var cs404 = C("CS404", "الذكاء الاصطناعي", "Artificial Intelligence", "#0369a1", "core");

        void Qualify(Instructor i, params Course[] cs) => i.QualifiedCourseIds.AddRange(cs.Select(c => c.Id));
        Qualify(ahmed, cs201, cs305); Qualify(sara, cs210, cs311, math201); Qualify(khaled, cs402, cs204); Qualify(yasser, cs404, cs204);
        Qualify(mona, cs201, cs305, cs204); Qualify(omar, math201, cs210, cs311); Qualify(karim, cs204, cs402);
        Qualify(dina, cs201, cs311, cs404); Qualify(tarek, cs402, cs404, cs305);

        var term = new AcademicTerm { InstitutionId = inst, Code = "FALL24", NameAr = "الفصل الدراسي الأول 2024", NameEn = "Fall 2024", StartDate = new DateOnly(2024, 9, 22), EndDate = new DateOnly(2025, 1, 9), IsCurrent = true };
        term.CalendarDays.Add(new TermCalendarDay { Date = new DateOnly(2024, 10, 6), Kind = CalendarDayKind.Holiday, NameAr = "عيد القوات المسلحة", NameEn = "Armed Forces Day" });
        db.AcademicTerms.Add(term);
        await db.SaveChangesAsync(ct);

        void Rule(OrgUnit unit, Course c, Guid type, Guid? kind, int perWeek, Instructor? instr, bool shared = false, int? duration = null) =>
            db.CurriculumRules.Add(new CurriculumRule
            {
                InstitutionId = inst, OrgUnitId = unit.Id, CourseId = c.Id, SessionTypeId = type, GroupKindId = kind, SessionsPerWeek = perWeek,
                DefaultInstructorId = instr?.Id, SharedAcrossGroups = shared, DurationSlots = duration,
            });
        // Year 3: lecture to the whole cohort, sections per section group, labs per lab group (instructor chosen from pool).
        Rule(y3, cs201, lecture.Id, cohort.Id, 2, ahmed);
        Rule(y3, cs201, sec.Id, section.Id, 1, mona);
        Rule(y3, cs201, labType.Id, labGroup.Id, 1, null);
        Rule(y3, cs204, lecture.Id, cohort.Id, 1, khaled);
        Rule(y3, cs204, labType.Id, labGroup.Id, 1, null);
        Rule(y3, cs210, lecture.Id, cohort.Id, 1, sara);
        Rule(y3, math201, lecture.Id, cohort.Id, 1, sara);
        Rule(y3, math201, sec.Id, section.Id, 1, omar);
        Rule(y3, cs305, lecture.Id, cohort.Id, 1, ahmed);
        // Year 4
        Rule(y4, cs402, lecture.Id, cohort.Id, 2, khaled);
        Rule(y4, cs402, sec.Id, section.Id, 1, karim);
        Rule(y4, cs311, lecture.Id, cohort.Id, 1, sara);
        Rule(y4, cs311, sec.Id, section.Id, 1, dina);
        Rule(y4, cs404, lecture.Id, cohort.Id, 1, yasser);
        Rule(y4, cs404, labType.Id, labGroup.Id, 1, null);
        await db.SaveChangesAsync(ct);
        await curriculum.GenerateAsync(term.Id, null, apply: true, ct);

        // A few availability preferences / blackouts.
        db.InstructorAvailabilities.Add(new Domain.Availability.InstructorAvailability { InstructorId = sara.Id, DayOfWeek = 4, SlotIndex = 5, State = AvailabilityState.Unavailable });
        db.InstructorAvailabilities.Add(new Domain.Availability.InstructorAvailability { InstructorId = sara.Id, DayOfWeek = 4, SlotIndex = 6, State = AvailabilityState.Unavailable });
        db.InstructorAvailabilities.Add(new Domain.Availability.InstructorAvailability { InstructorId = ahmed.Id, DayOfWeek = 0, SlotIndex = 0, State = AvailabilityState.Preferred });
        db.InstructorAvailabilities.Add(new Domain.Availability.InstructorAvailability { InstructorId = ahmed.Id, DayOfWeek = 2, SlotIndex = 1, State = AvailabilityState.Preferred });

        db.Schedules.Add(new Schedule { InstitutionId = inst, TermId = term.Id, Name = "Draft v1", Version = 1 });
        await db.SaveChangesAsync(ct);
        return (term.Id, ahmed.Id, firstSection!.Id);
    }

    // ------------------------------------------------------------------ Secondary school
    private async Task SeedSchool(Guid inst, CancellationToken ct)
    {
        tenant.Set(inst);
        var stage = await Lookup(db.OrgUnitTypes, inst, "STAGE");
        var grade = await Lookup(db.OrgUnitTypes, inst, "GRADE");
        var classKind = await Lookup(db.GroupKinds, inst, "CLASS");
        var classroom = await Lookup(db.RoomTypes, inst, "CLASSROOM");
        var lab = await Lookup(db.RoomTypes, inst, "LAB");
        var gym = await Lookup(db.RoomTypes, inst, "GYM");
        var teacher = await Lookup(db.InstructorTypes, inst, "TEACHER");
        var cls = await Lookup(db.SessionTypes, inst, "CLASS");
        var labType = await Lookup(db.SessionTypes, inst, "LAB");
        var activity = await Lookup(db.SessionTypes, inst, "ACTIVITY");

        var secStage = new OrgUnit { InstitutionId = inst, Code = "SEC", NameAr = "المرحلة الثانوية", NameEn = "Secondary stage", OrgUnitTypeId = stage.Id };
        db.OrgUnits.Add(secStage);
        var building = new Building { InstitutionId = inst, Code = "MAIN", NameAr = "المبنى الرئيسي", NameEn = "Main building" };
        db.Buildings.Add(building);
        var labRoom = new Room { InstitutionId = inst, Code = "SCI-LAB", NameAr = "معمل العلوم", NameEn = "Science lab", RoomTypeId = lab.Id, Capacity = 40, BuildingId = building.Id, Equipment = ["PROJECTOR"] };
        var pcLab = new Room { InstitutionId = inst, Code = "PC-LAB", NameAr = "معمل الحاسب", NameEn = "Computer lab", RoomTypeId = lab.Id, Capacity = 40, BuildingId = building.Id, Equipment = ["PC"] };
        var gymRoom = new Room { InstitutionId = inst, Code = "GYM", NameAr = "الملعب", NameEn = "Playground", RoomTypeId = gym.Id, Capacity = 120, BuildingId = building.Id };
        db.Rooms.AddRange(labRoom, pcLab, gymRoom);

        Instructor T(string code, string ar, string en) { var t = new Instructor { InstitutionId = inst, Code = code, NameAr = ar, NameEn = en, InstructorTypeId = teacher.Id, MaxHoursPerWeek = 18 }; db.Instructors.Add(t); return t; }
        Course C(string code, string ar, string en, params string[] tags) { var c = new Course { InstitutionId = inst, Code = code, NameAr = ar, NameEn = en, Tags = [.. tags] }; db.Courses.Add(c); return c; }

        var arabic = C("AR", "اللغة العربية", "Arabic", "heavy");
        var math = C("MATH", "الرياضيات", "Mathematics", "heavy");
        var english = C("EN", "اللغة الإنجليزية", "English");
        var physics = C("PHY", "الفيزياء", "Physics", "heavy");
        var chemistry = C("CHEM", "الكيمياء", "Chemistry");
        var religion = C("REL", "التربية الدينية", "Religious education");
        var pe = C("PE", "التربية الرياضية", "Physical education", "pe");
        var computer = C("ICT", "الحاسب الآلي", "Computer science");

        var tAr1 = T("T-AR1", "أ. محمود سعيد", "Mr. Mahmoud Saeed");
        var tAr2 = T("T-AR2", "أ. هدى عامر", "Ms. Hoda Amer");
        var tMath1 = T("T-MA1", "أ. سامي يوسف", "Mr. Samy Youssef");
        var tMath2 = T("T-MA2", "أ. رانيا كمال", "Ms. Rania Kamal");
        var tEn = T("T-EN", "أ. نادية فاروق", "Ms. Nadia Farouk");
        var tPhy = T("T-PH", "أ. وائل منصور", "Mr. Wael Mansour");
        var tChem = T("T-CH", "أ. إيمان حسين", "Ms. Eman Hussein");
        var tRel = T("T-RE", "أ. حسن عبد الله", "Mr. Hassan Abdallah");
        var tPe = T("T-PE", "أ. شريف مراد", "Mr. Sherif Morad");
        var tIct = T("T-IC", "أ. منة الله سمير", "Ms. Menna Samir");

        var term = new AcademicTerm { InstitutionId = inst, Code = "T1-2024", NameAr = "الفصل الدراسي الأول 2024/2025", NameEn = "Term 1 2024/2025", StartDate = new DateOnly(2024, 9, 29), EndDate = new DateOnly(2025, 1, 16), IsCurrent = true };
        db.AcademicTerms.Add(term);

        foreach (var (g, label) in new[] { ("10", "الأول الثانوي"), ("11", "الثاني الثانوي") })
        {
            var gradeUnit = new OrgUnit { InstitutionId = inst, Code = $"G{g}", NameAr = $"الصف {label}", NameEn = $"Grade {g}", OrgUnitTypeId = grade.Id, ParentId = secStage.Id };
            db.OrgUnits.Add(gradeUnit);
            foreach (var c in new[] { "A", "B" })
            {
                var room = new Room { InstitutionId = inst, Code = $"CR-{g}{c}", NameAr = $"فصل {g}{c}", NameEn = $"Classroom {g}{c}", RoomTypeId = classroom.Id, Capacity = 40, BuildingId = building.Id };
                db.Rooms.Add(room);
                db.StudentGroups.Add(new StudentGroup
                {
                    InstitutionId = inst, Code = $"{g}{c}", NameAr = $"فصل {g}/{c}", NameEn = $"Class {g}{c}", GroupKindId = classKind.Id,
                    StudentCount = 34, OrgUnitId = gradeUnit.Id, HomeRoomId = room.Id,
                });
            }
            var odd = g == "10";
            void Q(Course course, Guid type, int perWeek, Instructor t) => db.CurriculumRules.Add(new CurriculumRule
            {
                InstitutionId = inst, OrgUnitId = gradeUnit.Id, CourseId = course.Id, SessionTypeId = type, GroupKindId = classKind.Id,
                SessionsPerWeek = perWeek, DefaultInstructorId = t.Id,
            });
            Q(arabic, cls.Id, 5, odd ? tAr1 : tAr2);
            Q(math, cls.Id, 5, odd ? tMath1 : tMath2);
            Q(english, cls.Id, 4, tEn);
            Q(physics, cls.Id, 3, tPhy);
            Q(chemistry, labType.Id, 2, tChem);
            Q(religion, cls.Id, 2, tRel);
            Q(pe, activity.Id, 2, tPe);
            Q(computer, labType.Id, 2, tIct);
        }
        await db.SaveChangesAsync(ct);
        await curriculum.GenerateAsync(term.Id, null, apply: true, ct);
        // Computer sessions need PCs.
        foreach (var s in await db.Sessions.Where(s => s.CourseId == computer.Id).ToListAsync(ct)) s.RequiredEquipment = ["PC"];
        db.Schedules.Add(new Schedule { InstitutionId = inst, TermId = term.Id, Name = "Draft v1", Version = 1 });
        await db.SaveChangesAsync(ct);
    }
}
