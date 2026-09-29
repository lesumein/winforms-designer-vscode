using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using WinFormsDesigner.Engine;

namespace Engine.UnitTests;

/// <summary>A compiled BASE form for the derived-designer tests: its controls are the "inherited" ones a derived
/// .Designer.cs addresses without declaring them (public fields, as VS generates with Modifiers = Public).</summary>
public class CompatBaseForm : Form
{
    public Panel panel1;
    public Button baseButton;

    public CompatBaseForm()
    {
        panel1 = new Panel { Name = "panel1", Location = new Point(0, 0), Size = new Size(200, 100) };
        baseButton = new Button { Name = "baseButton", Location = new Point(10, 10) };
        panel1.Controls.Add(baseButton);
        Controls.Add(panel1);
    }
}

/// <summary>A control whose property type is an enum NESTED in the control class.</summary>
public class CompatScaleControl : Control
{
    public enum ScaleKind { Linear, Logarithmic }
    [DefaultValue(ScaleKind.Linear)]
    public ScaleKind Kind { get; set; }
}

/// <summary>
/// Designer shapes Visual Studio emits that used to force the compiled fallback (or were mis-rendered): protected
/// DoubleBuffered, data-object locals (DataGridViewCellStyle, MSChart elements), statements on inherited controls in a
/// derived designer, decimal values, nested enums, framework enums in localized .resx, and the z-order of a control
/// dropped onto a derived form.
/// </summary>
public sealed class DesignerCompatTests
{
    private sealed class Host : IIrHost
    {
        private static readonly Assembly[] Probe =
        {
            typeof(Control).Assembly, typeof(Color).Assembly, typeof(Font).Assembly, typeof(Point).Assembly,
            typeof(ISupportInitialize).Assembly, typeof(object).Assembly, typeof(DesignerCompatTests).Assembly,
        };
        public Type? ResolveType(string typeName)
        {
            foreach (var a in Probe)
            {
                var t = a.GetType(typeName, throwOnError: false);
                if (t != null) return t;
            }
            return Type.GetType(typeName, throwOnError: false);
        }
        public object CreateComponent(Type type, string name, bool withContainer) => Activator.CreateInstance(type)!;
        public object? ResolveResource(string key, bool isString) => null;
        public bool WasResourceRefused(string key) => false;
        public bool ApplyResources(object target, string key, out string? error) { error = "no test resources"; return false; }
    }

    private static IrDocument BuildFull(string src)
    {
        var doc = DesignerIrBuilder.Build(src);
        Assert.NotNull(doc);
        Assert.True(doc!.FullCoverage, string.Join(" | ", doc.UnrepresentableReasons));
        Assert.Null(IrValidate.Check(doc));
        return doc;
    }

    [Fact]
    public void DoubleBuffered_IsInterpreted_AndSetsTheProtectedFlag()
    {
        const string src = @"
namespace Demo {
  partial class Form1 {
    private void InitializeComponent() {
      this.SuspendLayout();
      this.ClientSize = new System.Drawing.Size(300, 200);
      this.DoubleBuffered = true;
      this.Name = ""Form1"";
      this.ResumeLayout(false);
    }
  }
}";
        var form = new Form();
        var res = DesignerIrExecutor.Execute(BuildFull(src), form, new Host());
        Assert.True(res.Ok, res.FailureReason);
        var flag = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)flag.GetValue(form)!);
    }

    [Fact]
    public void DataGridViewCellStyleLocal_IsConstructed_Configured_AndAssigned()
    {
        const string src = @"
namespace Demo {
  partial class Form1 {
    private System.Windows.Forms.DataGridView dataGridView1;
    private void InitializeComponent() {
      System.Windows.Forms.DataGridViewCellStyle dataGridViewCellStyle1 = new System.Windows.Forms.DataGridViewCellStyle();
      this.dataGridView1 = new System.Windows.Forms.DataGridView();
      ((System.ComponentModel.ISupportInitialize)(this.dataGridView1)).BeginInit();
      dataGridViewCellStyle1.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleCenter;
      dataGridViewCellStyle1.BackColor = System.Drawing.SystemColors.Control;
      dataGridViewCellStyle1.WrapMode = System.Windows.Forms.DataGridViewTriState.True;
      this.dataGridView1.ColumnHeadersDefaultCellStyle = dataGridViewCellStyle1;
      this.dataGridView1.Name = ""dataGridView1"";
      this.Controls.Add(this.dataGridView1);
      ((System.ComponentModel.ISupportInitialize)(this.dataGridView1)).EndInit();
    }
  }
}";
        var doc = BuildFull(src);
        Assert.Contains(doc.Statements, s => s is IrConstructLocalObject);
        var res = DesignerIrExecutor.Execute(doc, new Form(), new Host());
        Assert.True(res.Ok, res.FailureReason);
        var style = ((DataGridView)res.Instances["dataGridView1"]).ColumnHeadersDefaultCellStyle;
        Assert.Equal(DataGridViewContentAlignment.MiddleCenter, style.Alignment);
        Assert.Equal(DataGridViewTriState.True, style.WrapMode);
    }

    [Fact]
    public void ChartLocals_AreRepresented_ByTheClosedLocalObjectStatements()
    {
        // MSChart is not part of modern .NET, so this pins the syntax-only front-end + validator; the executor binds
        // the types by strong name on .NET Framework (net48 engine).
        const string src = @"
namespace Demo {
  partial class Form1 {
    private System.Windows.Forms.DataVisualization.Charting.Chart chart1;
    private void InitializeComponent() {
      System.Windows.Forms.DataVisualization.Charting.ChartArea chartArea1 = new System.Windows.Forms.DataVisualization.Charting.ChartArea();
      System.Windows.Forms.DataVisualization.Charting.Series series1 = new System.Windows.Forms.DataVisualization.Charting.Series();
      this.chart1 = new System.Windows.Forms.DataVisualization.Charting.Chart();
      ((System.ComponentModel.ISupportInitialize)(this.chart1)).BeginInit();
      chartArea1.AxisX.Maximum = 1000D;
      chartArea1.Name = ""ChartArea1"";
      this.chart1.ChartAreas.Add(chartArea1);
      series1.ChartArea = ""ChartArea1"";
      series1.ChartType = System.Windows.Forms.DataVisualization.Charting.SeriesChartType.Line;
      this.chart1.Series.Add(series1);
      this.Controls.Add(this.chart1);
      ((System.ComponentModel.ISupportInitialize)(this.chart1)).EndInit();
    }
  }
}";
        var doc = BuildFull(src);
        Assert.Equal(2, doc.Statements.OfType<IrConstructLocalObject>().Count());
        Assert.Equal(2, doc.Statements.OfType<IrAddLocalObject>().Count());
        var nested = doc.Statements.OfType<IrSetLocalObjectProp>().Single(p => p.PropertyPath.Count == 2);
        Assert.Equal(new[] { "AxisX", "Maximum" }, nested.PropertyPath);
    }

    [Fact]
    public void LocalOfAnUnlistedType_StaysAGap()
    {
        const string src = @"
namespace Demo {
  partial class Form1 {
    private void InitializeComponent() {
      System.Text.StringBuilder sb = new System.Text.StringBuilder();
      this.Name = ""Form1"";
    }
  }
}";
        var doc = DesignerIrBuilder.Build(src)!;
        Assert.False(doc.FullCoverage);
    }

    [Fact]
    public void Validator_RefusesALocalObjectOfAForgedType()
    {
        var doc = new IrDocument { DesignedTypeName = "Demo.Form1" };
        doc.Statements.Add(new IrConstructLocalObject { LocalName = "x", TypeName = "System.IO.FileStream" });
        Assert.NotNull(IrValidate.Check(doc));
    }

    [Fact]
    public void DerivedDesigner_StatementsOnInheritedControls_ReplayOntoTheBaseInstance()
    {
        const string src = @"
namespace Demo {
  partial class Form2 {
    private System.Windows.Forms.Button button1;
    private void InitializeComponent() {
      this.button1 = new System.Windows.Forms.Button();
      this.panel1.SuspendLayout();
      this.SuspendLayout();
      this.button1.Location = new System.Drawing.Point(20, 40);
      this.button1.Name = ""button1"";
      this.panel1.Controls.Add(this.button1);
      this.panel1.BackColor = System.Drawing.Color.Red;
      this.panel1.Controls.SetChildIndex(this.button1, 0);
      this.baseButton.Text = ""renamed"";
      this.Name = ""Form2"";
      this.panel1.ResumeLayout(false);
      this.ResumeLayout(false);
    }
  }
}";
        var doc = BuildFull(src);
        Assert.Single(doc.Statements.OfType<IrSetChildIndex>());
        var root = new CompatBaseForm();
        var res = DesignerIrExecutor.Execute(doc, root, new Host());
        Assert.True(res.Ok, res.FailureReason);
        Assert.Equal(Color.Red, root.panel1.BackColor);
        Assert.Equal("renamed", root.baseButton.Text);
        var added = (Button)res.Instances["button1"];
        Assert.Same(root.panel1, added.Parent);
        Assert.Equal(0, root.panel1.Controls.GetChildIndex(added)); // front of the inherited baseButton
    }

    [Fact]
    public void DecimalValue_IsInterpreted()
    {
        const string src = @"
namespace Demo {
  partial class Form1 {
    private System.Windows.Forms.NumericUpDown numericUpDown1;
    private void InitializeComponent() {
      this.numericUpDown1 = new System.Windows.Forms.NumericUpDown();
      this.numericUpDown1.DecimalPlaces = 1;
      this.numericUpDown1.Increment = new decimal(new int[] {
            1,
            0,
            0,
            65536});
      this.Controls.Add(this.numericUpDown1);
    }
  }
}";
        var res = DesignerIrExecutor.Execute(BuildFull(src), new Form(), new Host());
        Assert.True(res.Ok, res.FailureReason);
        Assert.Equal(0.1m, ((NumericUpDown)res.Instances["numericUpDown1"]).Increment);
    }

    [Fact]
    public void EnumNestedInAControlClass_Resolves()
    {
        const string src = @"
namespace Demo {
  partial class Form1 {
    private Engine.UnitTests.CompatScaleControl scale1;
    private void InitializeComponent() {
      this.scale1 = new Engine.UnitTests.CompatScaleControl();
      this.scale1.Kind = Engine.UnitTests.CompatScaleControl.ScaleKind.Logarithmic;
      this.Controls.Add(this.scale1);
    }
  }
}";
        var res = DesignerIrExecutor.Execute(BuildFull(src), new Form(), new Host());
        Assert.True(res.Ok, res.FailureReason);
        Assert.Equal(CompatScaleControl.ScaleKind.Logarithmic, ((CompatScaleControl)res.Instances["scale1"]).Kind);
    }

    [Fact]
    public void LocalizedResx_FrameworkEnumValue_IsApplied_ButAProjectTypeIsStillRefused()
    {
        const string resx = @"<?xml version=""1.0"" encoding=""utf-8""?>
<root>
  <data name=""textBox1.ScrollBars"" type=""System.Windows.Forms.ScrollBars, System.Windows.Forms""><value>Vertical</value></data>
  <data name=""textBox1.Multiline"" type=""System.Boolean, mscorlib""><value>True</value></data>
  <data name=""other.Mode"" type=""Demo.MyEnum, Demo""><value>A</value></data>
</root>";
        var resolver = SafeResxResolver.Parse(resx);
        var box = new TextBox();
        Assert.True(resolver.ApplyResources(box, "textBox1", out var error), error);
        Assert.Equal(ScrollBars.Vertical, box.ScrollBars);
        Assert.True(resolver.WasRefused("other.Mode"));
    }

    private const string DerivedDesigner = @"namespace Demo
{
    partial class Form2
    {
        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.Name = ""Form2"";
            this.ResumeLayout(false);
        }
    }
}
";

    private static InheritedOverrideEditResult OverrideInherited(string property, string propertyType, string value) =>
        DesignerInheritedOverrideEditor.TryApply(new InheritedOverrideEditRequest
        {
            SourceText = DerivedDesigner,
            FieldId = "baseButton",
            FieldTypeName = "System.Windows.Forms.Button",
            EffectiveAccessibility = "public",
            PropertyName = property,
            PropertyTypeName = propertyType,
            ValueExpression = value,
            ExpectedBaseIdentityToken = "token-1",
            ObservedBaseIdentityToken = "token-1",
        });

    [Theory]
    [InlineData("BackColor", "System.Drawing.Color", "System.Drawing.Color.Red")]
    [InlineData("ForeColor", "System.Drawing.Color", "System.Drawing.Color.FromArgb(((int)(((byte)(64)))), ((int)(((byte)(0)))), ((int)(((byte)(0)))))")]
    [InlineData("FlatStyle", "System.Windows.Forms.FlatStyle", "System.Windows.Forms.FlatStyle.Flat")]
    [InlineData("Font", "System.Drawing.Font", "new System.Drawing.Font(\"Arial\", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)))")]
    [InlineData("AutoSize", "System.Boolean", "true")]
    [InlineData("Padding", "System.Windows.Forms.Padding", "new System.Windows.Forms.Padding(2)")]
    public void InheritedOverride_AcceptsASimpleValuedProperty(string property, string type, string value)
    {
        var result = OverrideInherited(property, type, value);
        Assert.True(result.Safe, result.Reason);
        Assert.Equal(InheritedOverrideEditMode.Insert, result.Mode);
        Assert.Contains("this.baseButton." + property + " = " + value + ";", result.NewText);
    }

    [Theory]
    [InlineData("Name", "System.String", "\"renamed\"")]                                // identity stays with the base
    [InlineData("Tag", "System.Object", "\"x\"")]                                       // not a simple designer type
    [InlineData("BackColor", "System.Drawing.Color", "System.Drawing.Color.FromName(this.Text)")]
    [InlineData("ForeColor", "System.Drawing.Color", "GetColor()")]                     // arbitrary invocation
    [InlineData("Text", "System.String", "System.IO.File.ReadAllText(\"x\")")]
    [InlineData("BackColor", "System.Drawing.Color", "this.BackColor")]
    public void InheritedOverride_RefusesIdentityNonSimpleTypesAndUnsafeValues(string property, string type, string value)
    {
        var result = OverrideInherited(property, type, value);
        Assert.False(result.Safe);
    }

    [Fact]
    public void DropOnADerivedForm_BringsTheNewControlToTheFront()
    {
        const string designer = @"namespace Demo
{
    partial class Form2
    {
        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.Name = ""Form2"";
            this.ResumeLayout(false);
        }
    }
}
";
        string dir = Path.Combine(Path.GetTempPath(), "wfd-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string derivedFile = Path.Combine(dir, "Form2.Designer.cs");
            File.WriteAllText(derivedFile, designer);
            File.WriteAllText(Path.Combine(dir, "Form2.cs"), "namespace Demo { public partial class Form2 : Form1 { } }");
            var derived = DesignerRenderer.AddControl(derivedFile, "this", "Button");
            Assert.True(derived.Safe, derived.Reason);
            Assert.Contains("this.Controls.SetChildIndex(this.button1, 0);", derived.NewText);

            File.WriteAllText(Path.Combine(dir, "Form2.cs"), "namespace Demo { public partial class Form2 : System.Windows.Forms.Form { } }");
            var plain = DesignerRenderer.AddControl(derivedFile, "this", "Button");
            Assert.True(plain.Safe, plain.Reason);
            Assert.DoesNotContain("SetChildIndex", plain.NewText);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
