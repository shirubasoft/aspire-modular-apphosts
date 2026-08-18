using System.Xml.Linq;
using Xunit;

namespace CrapScore.Tests;

public sealed class CrapMetricTests
{
    [Fact]
    public void Calculate_combines_complexity_and_sequence_coverage()
    {
        var score = CrapMetric.Calculate(34, 40);

        Assert.Equal(283.696, score, precision: 3);
    }

    [Fact]
    public void Parse_reads_method_inputs_and_source_location_from_OpenCover()
    {
        var document = XDocument.Parse(
            """
            <CoverageSession>
              <Modules>
                <Module>
                  <ModuleName>Example.Library</ModuleName>
                  <Files>
                    <File uid="1" fullPath="/repo/src/Parser.cs" />
                  </Files>
                  <Classes>
                    <Class>
                      <Methods>
                        <Method cyclomaticComplexity="5" sequenceCoverage="80">
                          <Name>System.String Example.Parser::Parse(System.String)</Name>
                          <FileRef uid="1" />
                          <SequencePoints>
                            <SequencePoint sl="42" fileid="1" />
                          </SequencePoints>
                        </Method>
                      </Methods>
                    </Class>
                  </Classes>
                </Module>
              </Modules>
            </CoverageSession>
            """);

        var score = Assert.Single(OpenCoverCrapReport.Parse(document));

        Assert.Equal("Example.Library", score.Assembly);
        Assert.Equal("System.String Example.Parser::Parse(System.String)", score.Method);
        Assert.Equal("/repo/src/Parser.cs", score.SourceFile);
        Assert.Equal(42, score.SourceLine);
        Assert.Equal(5, score.CyclomaticComplexity);
        Assert.Equal(80, score.SequenceCoverage);
        Assert.Equal(5.2, score.Score, precision: 1);
    }
}
